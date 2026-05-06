// ── smpsh argument types ──────────────────────────────────────────────────────

enum ArgKind { Text, Flag, Var, List }

/// <summary>
/// A parsed argument.<br/>
/// Text  → plain word or quoted string<br/>
/// Flag  → !rf  (Flags = ['r','f'])<br/>
/// Var   → !key=value<br/>
/// List  → !name(a, b, c)
/// </summary>
record Arg(ArgKind Kind, string Raw, char[] Flags = null!, string Key = "", string Val = "", string[] Items = null!)
{
    public bool HasFlag(char f) => Kind == ArgKind.Flag && Array.IndexOf(Flags, f) >= 0;
    public bool HasFlag(string s) => Kind == ArgKind.Flag && s.All(f => Array.IndexOf(Flags, f) >= 0);
}

record Cmd(string Name, List<Arg> Args)
{
    public bool Flag(char f)        => Args.Any(a => a.HasFlag(f));
    public string? Var(string key)  => Args.FirstOrDefault(a => a.Kind == ArgKind.Var && string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.Val;
    public string[]? List(string k) => Args.FirstOrDefault(a => a.Kind == ArgKind.List && string.Equals(a.Key, k, StringComparison.OrdinalIgnoreCase))?.Items;
    public string[] Texts           => Args.Where(a => a.Kind == ArgKind.Text).Select(a => a.Val).ToArray();
}

enum Join { End, Seq, Also }   // ;  ,also,

record Statement(List<Cmd> Pipe, Join Next);

// ── Lexer ─────────────────────────────────────────────────────────────────────

static class Lexer
{
    /// <summary>Parse a full smpsh input line into a list of statements.</summary>
    public static List<Statement> Parse(string input)
    {
        // 1. Split on `, also,` and `;` while respecting quotes/parens
        var (chunks, joins) = SplitStatements(input);

        var statements = new List<Statement>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var pipe = SplitPipe(chunks[i].Trim())
                           .Select(ParseCmd)
                           .Where(c => !string.IsNullOrEmpty(c.Name))
                           .ToList();
            statements.Add(new Statement(pipe, i < joins.Count ? joins[i] : Join.End));
        }
        return statements;
    }

    // ── Statement splitter ────────────────────────────────────────────────────

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
            if (c == '(') { depth++; buf.Append(c); continue; }
            if (c == ')') { depth--; buf.Append(c); continue; }
            if (depth > 0) { buf.Append(c); continue; }

            // `, also,`
            if (i + 7 < input.Length && input[i..].StartsWith(", also,", StringComparison.OrdinalIgnoreCase))
            {
                chunks.Add(buf.ToString()); buf.Clear();
                joins.Add(Join.Also);
                i += 6; continue;
            }

            if (c == ';') { chunks.Add(buf.ToString()); buf.Clear(); joins.Add(Join.Seq); continue; }

            buf.Append(c);
        }

        if (buf.Length > 0) chunks.Add(buf.ToString());
        return (chunks, joins);
    }

    // ── Pipe splitter  (`>>`) ─────────────────────────────────────────────────

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
            if (c == '(') { depth++; buf.Append(c); continue; }
            if (c == ')') { depth--; buf.Append(c); continue; }
            if (depth == 0 && c == '>' && i + 1 < input.Length && input[i+1] == '>')
            {
                parts.Add(buf.ToString().Trim()); buf.Clear(); i++; continue;
            }
            buf.Append(c);
        }
        if (buf.Length > 0) parts.Add(buf.ToString().Trim());
        return parts;
    }

    // ── Command parser ────────────────────────────────────────────────────────

    static Cmd ParseCmd(string segment)
    {
        var tokens = Tokenise(segment);
        if (tokens.Count == 0) return new Cmd("", []);
        var name = tokens[0];
        var args  = tokens[1..].Select(ParseArg).ToList();
        return new Cmd(name, args);
    }

    static Arg ParseArg(string tok)
    {
        if (!tok.StartsWith('!'))
            return new Arg(ArgKind.Text, tok, Val: Unescape(tok));

        var body = tok[1..];

        // !name(items)
        var paren = body.IndexOf('(');
        if (paren > 0 && body.EndsWith(')'))
        {
            var key   = body[..paren];
            var inner = body[(paren+1)..^1];
            var items = inner.Split(',').Select(s => s.Trim()).ToArray();
            return new Arg(ArgKind.List, tok, Key: key, Items: items);
        }

        // !key=value
        var eq = body.IndexOf('=');
        if (eq > 0)
            return new Arg(ArgKind.Var, tok, Key: body[..eq], Val: body[(eq+1)..]);

        // !flags  (single or stacked)
        return new Arg(ArgKind.Flag, tok, Flags: body.ToCharArray());
    }

    // ── Tokeniser (respects quotes + parens) ─────────────────────────────────

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
            if (inQ)
            {
                if (c == qChar) { inQ = false; } else buf.Append(c);
                continue;
            }
            if (c is '"' or '\'') { inQ = true; qChar = c; continue; }
            if (c == '(') { depth++; buf.Append(c); continue; }
            if (c == ')') { depth--; buf.Append(c); continue; }
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (buf.Length > 0) { tokens.Add(buf.ToString()); buf.Clear(); }
                continue;
            }
            buf.Append(c);
        }
        if (buf.Length > 0) tokens.Add(buf.ToString());
        return tokens;
    }

    static string Unescape(string s) => s
        .Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
}
