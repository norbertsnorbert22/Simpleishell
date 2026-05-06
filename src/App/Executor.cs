using Spectre.Console;

static class Executor
{
    static string _cwd = Directory.GetCurrentDirectory();
    public static string Cwd => _cwd;

    /// <summary>Shell variable store — no prefix, plain names.</summary>
    static readonly Dictionary<string, ShObject> _vars =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Entry point ───────────────────────────────────────────────────────────

    public static int Run(List<Statement> statements)
    {
        int code = 0;
        foreach (var stmt in statements)
        {
            code = RunPipeline(stmt.Pipe);
            if (stmt.Next == Join.Also && code != 0) break;
        }
        return code;
    }

    // ── Pipeline (>>) ─────────────────────────────────────────────────────────
    // Each stage receives a ShObject? from the previous stage.
    // The final stage writes to stdout; intermediate stages capture output.

    static int RunPipeline(List<Cmd> pipe)
    {
        if (pipe.Count == 0) return 0;

        ShObject? carry = null;
        int code = 0;
        for (int i = 0; i < pipe.Count; i++)
        {
            bool last = i == pipe.Count - 1;
            (code, carry) = RunCmd(pipe[i], carry, capture: !last);
        }
        return code;
    }

    // ── Command dispatch ──────────────────────────────────────────────────────

    static (int code, ShObject? output) RunCmd(Cmd cmd, ShObject? stdin, bool capture)
    {
        // Closures that either collect into a buffer or write to console.
        var buf = capture ? new System.Text.StringBuilder() : null;
        void Out(string s) { if (buf != null) buf.AppendLine(s); else Console.WriteLine(s); }
        void Err(string s) => AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(s)}");

        ShObject? MakeOutput() => buf == null ? null : new StrObj(buf.ToString().TrimEnd());

        // ── Variable assignment:  name = value  ──────────────────────────────
        // Detected as Cmd(Name: "name", Args: [Arg("="), Arg(value)])
        if (cmd.Args.Count >= 1 && cmd.Args[0] is { Kind: ArgKind.Text, Val: "=" })
        {
            var rhs = cmd.Args.Count >= 2
                ? Resolve(cmd.Args[1])          // rhs is an arg (text, obj, etc.)
                : stdin ?? new StrObj("");       // or piped-in value
            _vars[cmd.Name] = rhs;
            return (0, null);
        }

        // ── Template stage:  say world! >> Hello, {}!  ───────────────────────
        // {} anywhere in the raw segment → replace with piped value.
        if (cmd.IsTemplate)
        {
            var piped  = stdin?.Display() ?? "";
            Out(cmd.Template.Replace("{}", piped));
            return (0, MakeOutput());
        }

        // ── Object literal used as a command:  {k: v} >> say  ────────────────
        // The object itself becomes the output of this pipeline stage.
        if (cmd.Name.StartsWith('{') && cmd.Name.EndsWith('}'))
        {
            var obj = EvalObjLiteral(cmd.Name);
            if (capture) return (0, obj);
            Console.WriteLine(obj.Display());
            return (0, null);
        }

        // ── Variable used as a command:  myvar >> say  ───────────────────────
        // If the name resolves to a variable, treat its value as the output.
        if (_vars.TryGetValue(cmd.Name, out var varVal) && cmd.Args.Count == 0)
        {
            if (capture) return (0, varVal);
            Console.WriteLine(varVal.Display());
            return (0, null);
        }

        // ── Named commands ────────────────────────────────────────────────────
        int code = cmd.Name.ToLowerInvariant() switch
        {
            "hi"      => CmdHi(Out),
            "say"     => CmdSay(cmd, stdin, Out, Err),
            "list"    => CmdList(cmd, stdin, Out, Err),
            "copy"    => CmdCopy(cmd, Err),
            "cd"      => CmdCd(cmd, Err),
            "pwd"     => Do(() => Out(_cwd)),
            "vars"    => CmdVars(Out),
            "exit"    => Do(() => Environment.Exit(0)),
            "help"    => CmdHelp(Out),
            _         => NotFound(cmd.Name, Err)
        };

        return (code, MakeOutput());
    }

    // ── hi ────────────────────────────────────────────────────────────────────

    static int CmdHi(Action<string> out_)
    {
        out_($"Hello, {Environment.UserName}!");
        return 0;
    }

    // ── say ───────────────────────────────────────────────────────────────────

    static int CmdSay(Cmd cmd, ShObject? stdin, Action<string> out_, Action<string> err)
    {
        // Resolve the value to display, in priority order:
        //   1. !file(path)  2. piped-in object  3. args joined as text
        ShObject value;

        var filePath = cmd.List("file")?.FirstOrDefault() ?? cmd.Var("file");
        if (filePath != null)
        {
            var full = Resolve(filePath);
            if (!File.Exists(full)) { err($"say: file not found: {filePath}"); return 1; }
            value = new StrObj(File.ReadAllText(full));
        }
        else if (stdin != null)
        {
            value = stdin;
        }
        else
        {
            // Join all text args, resolving each to its object value
            var parts = cmd.Args
                .Where(a => a.Kind is ArgKind.Text or ArgKind.Obj)
                .Select(Resolve)
                .Select(o => o.Display());
            value = new StrObj(string.Join(" ", parts));
        }

        // Case transforms
        string text = value.Display();
        bool upper  = cmd.Flag('u') || cmd.Var("Upper") != null || cmd.List("Upper") != null;
        bool lower  = cmd.Flag('l') || cmd.Var("Lower") != null || cmd.List("Lower") != null;
        if (upper) text = text.ToUpper();
        else if (lower) text = text.ToLower();

        text = text.Replace("\\n", "\n").Replace("\\t", "\t");
        out_(text);
        return 0;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    static int CmdList(Cmd cmd, ShObject? stdin, Action<string> out_, Action<string> err)
    {
        // Allow a piped string to act as the directory
        var dir  = cmd.RawTexts.FirstOrDefault()
                   ?? (stdin is StrObj s ? s.Value : null)
                   ?? _cwd;
        var full = ResolvePath(dir);

        if (!Directory.Exists(full)) { err($"list: no such directory: {dir}"); return 1; }

        var order  = cmd.Var("o") ?? "alpha";
        bool rev   = cmd.Flag('r');
        bool hidden= cmd.Flag('f');
        bool nfld  = cmd.Args.Any(a => a.Kind == ArgKind.Flag && new string(a.Flags) == "nfld");
        bool nfil  = cmd.Args.Any(a => a.Kind == ArgKind.Flag && new string(a.Flags) == "nfil");

        var files   = Directory.GetFiles(full).Select(f => new FileInfo(f))
                               .Where(f => hidden || !f.Name.StartsWith('.')).ToList();
        var folders = Directory.GetDirectories(full).Select(d => new DirectoryInfo(d))
                               .Where(d => hidden || !d.Name.StartsWith('.')).ToList();

        IEnumerable<FileInfo> sf = order switch
        {
            "size" => files.OrderBy(f => f.Length),
            "date" => files.OrderBy(f => f.LastWriteTime),
            _      => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };
        if (rev) sf = sf.Reverse();

        IEnumerable<DirectoryInfo> sd = order switch
        {
            "date" => folders.OrderBy(d => d.LastWriteTime),
            _      => folders.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        };
        if (rev) sd = sd.Reverse();

        if (!nfil)
        {
            out_("- Files");
            foreach (var f in sf)
            {
                out_($"  | - {f.Extension.TrimStart('.').ToUpper()} ({FormatSize(f.Length)})");
                out_($"      {f.Name}");
            }
            if (!files.Any()) out_("  (none)");
        }
        if (!nfld)
        {
            out_("- Folders");
            foreach (var d in sd) out_($"  -- {d.Name}");
            if (!folders.Any()) out_("  (none)");
        }
        return 0;
    }

    // ── copy ──────────────────────────────────────────────────────────────────

    static int CmdCopy(Cmd cmd, Action<string> err)
    {
        var texts = cmd.RawTexts;
        if (texts.Length < 2) { err("copy: usage: copy [source] [destination]"); return 1; }
        var src   = ResolvePath(texts[0]);
        var dest  = ResolvePath(texts[1]);
        bool rec   = cmd.Flag('r');
        bool force = cmd.Flag('f');

        if (Directory.Exists(src))
        {
            if (!rec) { err("copy: use !r to copy a directory"); return 1; }
            CopyDir(src, dest, force);
        }
        else if (File.Exists(src))
        {
            if (File.Exists(dest) && !force) { err("copy: destination exists, use !f to overwrite"); return 1; }
            File.Copy(src, dest, overwrite: force);
        }
        else { err($"copy: source not found: {texts[0]}"); return 1; }
        return 0;
    }

    static void CopyDir(string src, string dest, bool force)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), force);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)), force);
    }

    // ── cd ────────────────────────────────────────────────────────────────────

    static int CmdCd(Cmd cmd, Action<string> err)
    {
        var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = cmd.RawTexts.FirstOrDefault() switch
        {
            null or "~" => home,
            "-"         => Environment.GetEnvironmentVariable("OLDPWD") ?? _cwd,
            var t       => ResolvePath(t)
        };
        if (!Directory.Exists(target)) { err($"cd: no such directory: {target}"); return 1; }
        Environment.SetEnvironmentVariable("OLDPWD", _cwd);
        _cwd = target;
        Directory.SetCurrentDirectory(_cwd);
        return 0;
    }

    // ── vars ──────────────────────────────────────────────────────────────────

    static int CmdVars(Action<string> out_)
    {
        if (_vars.Count == 0) { out_("(no variables set)"); return 0; }
        foreach (var (k, v) in _vars)
            out_($"  {k} = {v.Display()}");
        return 0;
    }

    // ── help ──────────────────────────────────────────────────────────────────

    static int CmdHelp(Action<string> out_)
    {
        out_("""
            smpsh — Simpleishell

            Commands:
              hi                        greet the user
              say [text | var]          display text, a variable, or a piped object
              say !file(path)           display file contents
              list [dir]                list files and folders
              copy [src] [dest]         copy a file
              copy !r [src] [dest]      copy a directory recursively
              cd [dir]                  change directory
              pwd                       print working directory
              vars                      list all shell variables
              exit                      exit the shell

            Operators:
              >>               pipe an object to the next command
              , also,          run next only if previous succeeded
              ;                run next unconditionally

            Variables (no prefix):
              name = John               assign a string
              person = {n: John, a: 30} assign an object
              say name                  expand a variable
              name >> say               pipe a variable

            Objects:
              {key: value, ...}         inline object literal
              {k: v} >> say             pipe an object into say

            Argument format:
              !flag  !abc               single / stacked flags
              !key=value                named option
              !name(a, b)               list argument
            """);
        return 0;
    }

    // ── Object & variable resolution ──────────────────────────────────────────

    /// <summary>
    /// Resolve an <see cref="Arg"/> to its runtime <see cref="ShObject"/>.<br/>
    /// • Obj  → evaluate the literal fields<br/>
    /// • Text → check variable store first, otherwise treat as a string<br/>
    /// • Var  → the !key=val string value wrapped in StrObj<br/>
    /// </summary>
    static ShObject Resolve(Arg arg) => arg.Kind switch
    {
        ArgKind.Obj  => EvalObjLiteral(arg.Raw),
        ArgKind.Text => _vars.TryGetValue(arg.Val, out var v) ? v : new StrObj(arg.Val),
        ArgKind.Var  => new StrObj(arg.Val),
        _            => new StrObj(arg.Raw)
    };

    /// <summary>Resolve a plain path string (handles ~ expansion).</summary>
    static string Resolve(string path)
    {
        path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return path;
    }

    static string ResolvePath(string path)
    {
        path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_cwd, path));
    }

    /// <summary>Build a <see cref="DictObj"/> from a <c>{key: val, ...}</c> token.</summary>
    static ShObject EvalObjLiteral(string token)
    {
        var inner  = token.TrimStart('{').TrimEnd('}');
        var fields = new Dictionary<string, ShObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in SplitCommas(inner))
        {
            var colon = pair.IndexOf(':');
            if (colon < 0) continue;
            var key = pair[..colon].Trim();
            var val = pair[(colon + 1)..].Trim();
            if (key.Length == 0) continue;
            // Value is itself a variable reference or a literal string
            fields[key] = _vars.TryGetValue(val, out var v) ? v : new StrObj(val);
        }
        return new DictObj(fields);
    }

    static List<string> SplitCommas(string s)
    {
        var parts = new List<string>();
        var buf   = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char c in s)
        {
            if (c is '{' or '(') depth++;
            if (c is '}' or ')') depth--;
            if (c == ',' && depth == 0) { parts.Add(buf.ToString()); buf.Clear(); }
            else buf.Append(c);
        }
        if (buf.Length > 0) parts.Add(buf.ToString());
        return parts;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static int Do(Action a)                              { a(); return 0; }
    static int NotFound(string n, Action<string> err)    { err($"'{n}': command not found"); return 127; }

    static string FormatSize(long b) => b switch
    {
        < 1024               => $"{b} B",
        < 1024 * 1024        => $"{b / 1024} KB",
        < 1024 * 1024 * 1024 => $"{b / 1024 / 1024} MB",
        _                    => $"{b / 1024 / 1024 / 1024} GB"
    };
}
