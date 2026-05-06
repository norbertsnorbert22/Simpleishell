using Spectre.Console;

static class Executor
{
    static string _cwd = Directory.GetCurrentDirectory();
    public static string Cwd => _cwd;

    // ── Entry point ───────────────────────────────────────────────────────────

    public static int Run(List<Statement> statements)
    {
        int code = 0;
        foreach (var stmt in statements)
        {
            code = RunPipeline(stmt.Pipe);
            if (stmt.Next == Join.Also && code != 0) break;  // , also, = short-circuit on fail
        }
        return code;
    }

    // ── Pipeline (>>) ─────────────────────────────────────────────────────────

    static int RunPipeline(List<Cmd> pipe)
    {
        if (pipe.Count == 0) return 0;
        if (pipe.Count == 1) return RunCmd(pipe[0], null, null);

        string? prevOutput = null;
        int code = 0;
        for (int i = 0; i < pipe.Count; i++)
        {
            var outBuf = i < pipe.Count - 1 ? new System.Text.StringBuilder() : null;
            code = RunCmd(pipe[i], prevOutput, outBuf);
            prevOutput = outBuf?.ToString();
        }
        return code;
    }

    // ── Command dispatch ──────────────────────────────────────────────────────

    static int RunCmd(Cmd cmd, string? stdin, System.Text.StringBuilder? stdout)
    {
        void Out(string s) { if (stdout != null) stdout.AppendLine(s); else Console.WriteLine(s); }
        void Err(string s) => AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(s)}");

        return cmd.Name.ToLowerInvariant() switch
        {
            "hi"   => CmdHi(Out),
            "say"  => CmdSay(cmd, stdin, Out, Err),
            "list" => CmdList(cmd, Out, Err),
            "copy" => CmdCopy(cmd, Err),
            "cd"   => CmdCd(cmd, Err),
            "pwd"  => Do(() => Out(_cwd)),
            "exit" => Do(() => Environment.Exit(0)),
            "help" => CmdHelp(Out),
            _      => NotFound(cmd.Name, Err)
        };
    }

    // ── hi ────────────────────────────────────────────────────────────────────

    static int CmdHi(Action<string> out_)
    {
        out_($"Hello, {Environment.UserName}!");
        return 0;
    }

    // ── say ───────────────────────────────────────────────────────────────────

    static int CmdSay(Cmd cmd, string? stdin, Action<string> out_, Action<string> err)
    {
        string text;

        var filePath = cmd.List("file")?.FirstOrDefault() ?? cmd.Var("file");
        if (filePath != null)
        {
            var full = Resolve(filePath);
            if (!File.Exists(full)) { err($"say: file not found: {filePath}"); return 1; }
            text = File.ReadAllText(full);
        }
        else if (stdin != null)
        {
            text = stdin.TrimEnd();
        }
        else
        {
            text = string.Join(" ", cmd.Texts);
        }

        if (cmd.Flag('u') || cmd.Var("Upper") != null || cmd.List("Upper") != null)
            text = text.ToUpper();
        else if (cmd.Flag('l') || cmd.Var("Lower") != null || cmd.List("Lower") != null)
            text = text.ToLower();

        // Handle \n escape sequences in text
        text = text.Replace("\\n", "\n").Replace("\\t", "\t");

        out_(text);
        return 0;
    }

    // ── list ──────────────────────────────────────────────────────────────────

    static int CmdList(Cmd cmd, Action<string> out_, Action<string> err)
    {
        var dir   = cmd.Texts.FirstOrDefault() ?? _cwd;
        var full  = Resolve(dir);

        if (!Directory.Exists(full)) { err($"list: no such directory: {dir}"); return 1; }

        var order   = cmd.Var("o") ?? "alpha";
        bool rev    = cmd.Flag('r');
        bool noFold = cmd.Flag('n') && cmd.Args.Any(a => a.Kind == ArgKind.Flag && new string(a.Flags) is "nfld");
        bool noFile = cmd.Flag('n') && cmd.Args.Any(a => a.Kind == ArgKind.Flag && new string(a.Flags) is "nfil");
        bool hidden = cmd.Flag('f');

        // Parse !nfld and !nfil properly (they're multi-char flags used as keywords)
        bool nfld = cmd.Args.Any(a => a.Kind == ArgKind.Flag && new string(a.Flags) == "nfld");
        bool nfil = cmd.Args.Any(a => a.Kind == ArgKind.Flag && new string(a.Flags) == "nfil");

        var files   = Directory.GetFiles(full)
                               .Select(f => new FileInfo(f))
                               .Where(f => hidden || !f.Name.StartsWith('.'))
                               .ToList();
        var folders = Directory.GetDirectories(full)
                               .Select(d => new DirectoryInfo(d))
                               .Where(d => hidden || !d.Name.StartsWith('.'))
                               .ToList();

        IEnumerable<FileInfo> sortedFiles = order switch
        {
            "size" => files.OrderBy(f => f.Length),
            "date" => files.OrderBy(f => f.LastWriteTime),
            _      => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };
        if (rev) sortedFiles = sortedFiles.Reverse();

        IEnumerable<DirectoryInfo> sortedDirs = order switch
        {
            "date" => folders.OrderBy(d => d.LastWriteTime),
            _      => folders.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        };
        if (rev) sortedDirs = sortedDirs.Reverse();

        if (!nfil)
        {
            out_("- Files");
            foreach (var f in sortedFiles)
            {
                var size = FormatSize(f.Length);
                out_($"  | - {f.Extension.TrimStart('.').ToUpper()} ({size})");
                out_($"      {f.Name}");
            }
            if (!files.Any()) out_("  (none)");
        }

        if (!nfld)
        {
            out_("- Folders");
            foreach (var d in sortedDirs)
                out_($"  -- {d.Name}");
            if (!folders.Any()) out_("  (none)");
        }

        return 0;
    }

    // ── copy ──────────────────────────────────────────────────────────────────

    static int CmdCopy(Cmd cmd, Action<string> err)
    {
        var texts = cmd.Texts;
        if (texts.Length < 2) { err("copy: usage: copy [source] [destination]"); return 1; }

        var src  = Resolve(texts[0]);
        var dest = Resolve(texts[1]);
        bool rec   = cmd.Flag('r');
        bool force = cmd.Flag('f');

        if (Directory.Exists(src))
        {
            if (!rec) { err("copy: use !r to copy a directory"); return 1; }
            CopyDir(src, dest, force);
        }
        else if (File.Exists(src))
        {
            if (File.Exists(dest) && !force) { err($"copy: destination exists, use !f to overwrite"); return 1; }
            File.Copy(src, dest, overwrite: force);
        }
        else { err($"copy: source not found: {texts[0]}"); return 1; }

        return 0;
    }

    static void CopyDir(string src, string dest, bool force)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, target, overwrite: force);
        }
        foreach (var dir in Directory.GetDirectories(src))
            CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)), force);
    }

    // ── cd ────────────────────────────────────────────────────────────────────

    static int CmdCd(Cmd cmd, Action<string> err)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = cmd.Texts.FirstOrDefault() switch
        {
            null or "~" => home,
            "-"         => Environment.GetEnvironmentVariable("OLDPWD") ?? _cwd,
            var t       => Resolve(t)
        };
        if (!Directory.Exists(target)) { err($"cd: no such directory: {target}"); return 1; }
        Environment.SetEnvironmentVariable("OLDPWD", _cwd);
        _cwd = target;
        Directory.SetCurrentDirectory(_cwd);
        return 0;
    }

    // ── help ──────────────────────────────────────────────────────────────────

    static int CmdHelp(Action<string> out_)
    {
        out_("""
            smpsh — Simpleishell

            Commands:
              hi                        say hello
              say [text]                output text
              say !file(path)           output file contents
              list [dir]                list files and folders
              copy [src] [dest]         copy a file
              copy !r [src] [dest]      copy a directory recursively
              cd [dir]                  change directory
              pwd                       print working directory
              exit                      exit the shell

            Operators:
              >>               pipe output to next command
              , also,          run next command only if previous succeeded
              ;                run next command unconditionally

            Argument format:
              !flag            single flag  (e.g. !r  !f)
              !abc             stacked flags (e.g. !rvf)
              !key=value       variable     (e.g. !o=size)
              !name(a, b)      list         (e.g. !file(readme.md))
            """);
        return 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string Resolve(string path)
    {
        path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_cwd, path));
    }

    static int Do(Action a)                        { a(); return 0; }
    static int NotFound(string name, Action<string> err) { err($"'{name}': command not found"); return 127; }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024 / 1024} MB",
        _                    => $"{bytes / 1024 / 1024 / 1024} GB"
    };
}
