/// <summary>A single command with its arguments and optional I/O redirects.</summary>
record Cmd(List<string> Args, string? Stdin = null, string? Stdout = null, bool Append = false);

/// <summary>
/// Tokenises a shell line into a list of piped <see cref="Cmd"/>s.
/// Handles: single/double quotes, backslash escapes, $VAR / ${VAR}, pipes,
/// input/output redirects (< > >>).
/// </summary>
static class Lexer
{
    public static List<Cmd> Parse(string input, string cwd)
    {
        var pipeline = new List<Cmd>();
        var args     = new List<string>();
        string? redirectIn = null, redirectOut = null;
        bool append = false;
        bool nextIsIn = false, nextIsOut = false;

        foreach (var tok in Tokenise(input))
        {
            switch (tok)
            {
                case "|":
                    if (args.Count > 0) pipeline.Add(new Cmd([.. args], redirectIn, redirectOut, append));
                    args.Clear(); redirectIn = redirectOut = null; append = false;
                    break;
                case "<":  nextIsIn  = true; break;
                case ">":  nextIsOut = true; append = false; break;
                case ">>": nextIsOut = true; append = true;  break;
                default:
                    if      (nextIsIn)  { redirectIn  = Resolve(tok, cwd); nextIsIn  = false; }
                    else if (nextIsOut) { redirectOut = Resolve(tok, cwd); nextIsOut = false; }
                    else                  args.Add(tok);
                    break;
            }
        }

        if (args.Count > 0) pipeline.Add(new Cmd([.. args], redirectIn, redirectOut, append));
        return pipeline;
    }

    static string Resolve(string path, string cwd) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));

    static IEnumerable<string> Tokenise(string input)
    {
        int i = 0;
        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }

            // Two-char operators
            if (i + 1 < input.Length && input[i..(i+2)] is ">>" or "&&" or "||")
                { yield return input[i..(i+2)]; i += 2; continue; }

            // Single-char operators
            if (input[i] is '|' or '<' or '>')
                { yield return input[i].ToString(); i++; continue; }

            yield return ReadWord(input, ref i);
        }
    }

    static string ReadWord(string input, ref int i)
    {
        var sb = new System.Text.StringBuilder();
        while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] is not ('|' or '<' or '>'))
        {
            char c = input[i];
            if (c == '\'') { i++; while (i < input.Length && input[i] != '\'') sb.Append(input[i++]); if (i < input.Length) i++; }
            else if (c == '"')  { i++; while (i < input.Length && input[i] != '"')  { sb.Append(Escape(input, ref i)); } if (i < input.Length) i++; }
            else if (c == '\\') { i++; if (i < input.Length) sb.Append(input[i++]); }
            else if (c == '$')  { sb.Append(ReadVar(input, ref i)); }
            else                { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    static char Escape(string s, ref int i)
    {
        if (s[i] == '\\' && i + 1 < s.Length) { i++; return s[i] switch { 'n'=>'\n','t'=>'\t', var x=>x }; }
        return s[i++];
    }

    static string ReadVar(string input, ref int i)
    {
        i++; // skip $
        if (i >= input.Length) return "$";
        if (input[i] == '?') { i++; return Environment.GetEnvironmentVariable("?") ?? "0"; }
        if (input[i] == '{')
        {
            i++;
            int s = i; while (i < input.Length && input[i] != '}') i++;
            var n = input[s..i]; if (i < input.Length) i++;
            return Environment.GetEnvironmentVariable(n) ?? "";
        }
        int start = i;
        while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
        return Environment.GetEnvironmentVariable(input[start..i]) ?? "";
    }
}
