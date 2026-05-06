// ── Runtime object model ──────────────────────────────────────────────────────

/// <summary>Every value in smpsh is a ShObject (string or dict).</summary>
abstract record ShObject
{
    /// <summary>Human-readable form used by <c>say</c> and pipe display.</summary>
    public abstract string Display();
}

record StrObj(string Value) : ShObject
{
    public override string Display() => Value;
    public override string ToString() => Value;
}

record DictObj(IReadOnlyDictionary<string, ShObject> Fields) : ShObject
{
    public override string Display() =>
        "{ " + string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value.Display()}")) + " }";
    public override string ToString() => Display();
}

// ── smpsh parsed argument types ───────────────────────────────────────────────

enum ArgKind { Text, Flag, Var, List, Obj }

/// <summary>
/// A parsed argument.<br/>
/// Text  → plain word or quoted string (may resolve to a variable at runtime)<br/>
/// Flag  → <c>!rf</c>  (Flags = ['r','f'])<br/>
/// Var   → <c>!key=value</c><br/>
/// List  → <c>!name(a, b, c)</c><br/>
/// Obj   → <c>{key: val, ...}</c> inline object literal
/// </summary>
record Arg(ArgKind Kind, string Raw,
           char[]  Flags = null!,
           string  Key   = "",
           string  Val   = "",
           string[] Items = null!,
           IReadOnlyDictionary<string, string>? ObjFields = null)
{
    public bool HasFlag(char f) => Kind == ArgKind.Flag && Array.IndexOf(Flags, f) >= 0;
}

record Cmd(string Name, List<Arg> Args, bool IsTemplate = false, string Template = "")
{
    public bool     Flag(char f)       => Args.Any(a => a.HasFlag(f));
    public string?  Var(string key)    => Args.FirstOrDefault(a => a.Kind == ArgKind.Var
                                              && string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.Val;
    public string[]? List(string k)   => Args.FirstOrDefault(a => a.Kind == ArgKind.List
                                              && string.Equals(a.Key, k, StringComparison.OrdinalIgnoreCase))?.Items;
    /// <summary>All Text args (raw string values, before variable resolution).</summary>
    public string[] RawTexts           => Args.Where(a => a.Kind == ArgKind.Text).Select(a => a.Val).ToArray();
}

enum Join { End, Seq, Also }

record Statement(List<Cmd> Pipe, Join Next);

// ── Lexer ─────────────────────────────────────────────────────────────────────

static class Lexer
{
    /// <summary>Parse a full smpsh input line into a list of statements.</summary>
    public static List<Statement> Parse(string input)
    {
        var (chunks, joins) = SplitStatements(input.Trim());

        var statements = new List<Statement>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var pipe = SplitPipe(chunks[i].Trim())
                           .Select(ParseCmd)
                           .Where(c => c.Name.Length > 0 || c.IsTemplate)
                           .ToList();
            statements.Add(new Statement(pipe, i < joins.Count ? joins[i] : Join.End));
        }
        return statements;
    }

    // ── Statement splitter (respects quotes, (), {}) ──────────────────────────

    static (List<string> chunks, List<Join> joins) SplitStatements(string input)
    {
        var chunks = new List<string>();
        var joins  = new List<Join>();
        var buf    = new System.Text.StringBuilder();
        int depth  = 0;
        bool inQ   = false;
        char qChar = ' ';

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (inQ) { buf.Append(c); if (c == qChar) inQ = false; continue; }
            if (c is '"' or '\'') { inQ = true; qChar = c; buf.Append(c); continue; }
            if (c is '(' or '{') { depth++; buf.Append(c); continue; }
            if (c is ')' or '}') { depth--; buf.Append(c); continue; }
            if (depth > 0) { buf.Append(c); continue; }

            if (i + 7 < input.Length && input[i..].StartsWith(", also,", StringComparison.OrdinalIgnoreCase))
            { chunks.Add(buf.ToString()); buf.Clear(); joins.Add(Join.Also); i += 6; continue; }

            if (c == ';') { chunks.Add(buf.ToString()); buf.Clear(); joins.Add(Join.Seq); continue; }

            buf.Append(c);
        }
        if (buf.Length > 0) chunks.Add(buf.ToString());
        return (chunks, joins);
    }

    // ── Pipe splitter (`>>`, respects quotes, (), {}) ─────────────────────────

    static List<string> SplitPipe(string input)
    {
        var parts  = new List<string>();
        var buf    = new System.Text.StringBuilder();
        bool inQ   = false;
        char qChar = ' ';
        int depth  = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (inQ) { buf.Append(c); if (c == qChar) inQ = false; continue; }
            if (c is '"' or '\'') { inQ = true; qChar = c; buf.Append(c); continue; }
            if (c is '(' or '{') { depth++; buf.Append(c); continue; }
            if (c is ')' or '}') { depth--; buf.Append(c); continue; }
            if (depth == 0 && c == '>' && i + 1 < input.Length && input[i + 1] == '>')
            { parts.Add(buf.ToString().Trim()); buf.Clear(); i++; continue; }
            buf.Append(c);
        }
        if (buf.Length > 0) parts.Add(buf.ToString().Trim());
        return parts;
    }

    // ── Command parser ────────────────────────────────────────────────────────

    static Cmd ParseCmd(string segment)
    {
        // If {} appears anywhere in the raw segment, it's a format template.
        // Don't tokenise — the whole segment is the template string.
        if (segment.Contains("{}"))
            return new Cmd("", [], IsTemplate: true, Template: segment);

        var tokens = Tokenise(segment);
        if (tokens.Count == 0) return new Cmd("", []);
        var name = tokens[0];
        var args  = tokens[1..].Select(ParseArg).ToList();
        return new Cmd(name, args);
    }

    // ── Argument parser ───────────────────────────────────────────────────────

    static Arg ParseArg(string tok)
    {
        // {key: val, ...} object literal
        if (tok.StartsWith('{') && tok.EndsWith('}'))
            return new Arg(ArgKind.Obj, tok, ObjFields: ParseObjFields(tok[1..^1]));

        // !-prefixed smpsh args
        if (tok.StartsWith('!'))
        {
            var body  = tok[1..];
            var paren = body.IndexOf('(');
            if (paren > 0 && body.EndsWith(')'))
            {
                var items = body[(paren + 1)..^1].Split(',').Select(s => s.Trim()).ToArray();
                return new Arg(ArgKind.List, tok, Key: body[..paren], Items: items);
            }
            var eq = body.IndexOf('=');
            if (eq > 0)
                return new Arg(ArgKind.Var, tok, Key: body[..eq], Val: body[(eq + 1)..]);
            return new Arg(ArgKind.Flag, tok, Flags: body.ToCharArray());
        }

        // plain text (variable reference resolved at runtime by executor)
        return new Arg(ArgKind.Text, tok, Val: Unescape(tok));
    }

    /// <summary>Parse the interior of <c>{key: val, key2: val2}</c>.</summary>
    static IReadOnlyDictionary<string, string> ParseObjFields(string inner)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Split on commas at depth 0
        var pairs = SplitCommas(inner);
        foreach (var pair in pairs)
        {
            var colon = pair.IndexOf(':');
            if (colon < 0) continue;
            var key = pair[..colon].Trim();
            var val = pair[(colon + 1)..].Trim();
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
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

    // ── Tokeniser (respects quotes, (), {}) ───────────────────────────────────

    static List<string> Tokenise(string s)
    {
        var tokens = new List<string>();
        var buf    = new System.Text.StringBuilder();
        bool inQ   = false;
        char qChar = ' ';
        int depth  = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inQ) { if (c == qChar) inQ = false; else buf.Append(c); continue; }
            if (c is '"' or '\'') { inQ = true; qChar = c; continue; }
            if (c is '(' or '{') { depth++; buf.Append(c); continue; }
            if (c is ')' or '}') { depth--; buf.Append(c); continue; }
            if (char.IsWhiteSpace(c) && depth == 0)
            { if (buf.Length > 0) { tokens.Add(buf.ToString()); buf.Clear(); } continue; }
            buf.Append(c);
        }
        if (buf.Length > 0) tokens.Add(buf.ToString());
        return tokens;
    }

    static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
}
