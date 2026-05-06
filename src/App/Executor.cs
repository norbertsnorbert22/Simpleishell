using System.Diagnostics;
using Spectre.Console;

/// <summary>
/// Runs a parsed pipeline: builtins are handled in-process,
/// external commands are launched as child processes, stdout of each
/// is piped to stdin of the next.
/// </summary>
static class Executor
{
    public static async Task<int> RunAsync(List<Cmd> pipeline, List<string> history, ref string cwd)
    {
        if (pipeline.Count == 0) return 0;

        // Single command — fast path (most common case)
        if (pipeline.Count == 1)
            return await RunOne(pipeline[0], history, ref cwd, Console.In, Console.Out);

        // Multi-stage pipe
        TextReader reader = Console.In;
        int code = 0;

        for (int i = 0; i < pipeline.Count; i++)
        {
            bool last = i == pipeline.Count - 1;
            var (pipeRead, pipeWrite) = last ? (null, null) : Pipe();

            var writer = last ? Console.Out : new StreamWriter(pipeWrite!) { AutoFlush = true };
            code = await RunOne(pipeline[i], history, ref cwd, reader, writer);

            if (!last)
            {
                await writer.FlushAsync();
                pipeWrite!.Close();
                reader = new StreamReader(pipeRead!);
            }
        }

        return code;
    }

    static async Task<int> RunOne(Cmd cmd, List<string> history, ref string cwd,
                                   TextReader stdin, TextWriter stdout)
    {
        if (cmd.Args.Count == 0) return 0;
        var (name, args) = (cmd.Args[0], cmd.Args[1..].ToArray());

        // ── Builtins ─────────────────────────────────────────────────────────
        switch (name)
        {
            case "exit":
                Environment.Exit(args.Length > 0 && int.TryParse(args[0], out var x) ? x : 0);
                return 0;

            case "cd":
            {
                var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var target = args.Length == 0 ? home
                           : args[0] == "-"   ? Environment.GetEnvironmentVariable("OLDPWD") ?? cwd
                           : args[0] == "~"   ? home
                           : Path.IsPathRooted(args[0]) ? args[0]
                           : Path.GetFullPath(Path.Combine(cwd, args[0]));
                if (!Directory.Exists(target)) { Err($"cd: {target}: no such directory"); return 1; }
                Environment.SetEnvironmentVariable("OLDPWD", cwd);
                cwd = Directory.GetCurrentDirectory() == cwd ? (Directory.SetCurrentDirectory(target), target).Item2 : target;
                Directory.SetCurrentDirectory(cwd);
                return 0;
            }

            case "pwd":
                stdout.WriteLine(cwd);
                return 0;

            case "echo":
            {
                bool noNl = args.Length > 0 && args[0] == "-n";
                var text  = string.Join(" ", noNl ? args[1..] : args);
                if (noNl) stdout.Write(text); else stdout.WriteLine(text);
                return 0;
            }

            case "export":
                foreach (var a in args)
                {
                    var eq = a.IndexOf('=');
                    if (eq < 0) continue;
                    Environment.SetEnvironmentVariable(a[..eq], a[(eq+1)..]);
                }
                return 0;

            case "env":
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                    stdout.WriteLine($"{e.Key}={e.Value}");
                return 0;

            case "history":
                for (int i = 0; i < history.Count; i++)
                    stdout.WriteLine($"  {i+1,4}  {history[i]}");
                return 0;

            case "help":
                AnsiConsole.MarkupLine("""
                    [bold chocolate1]Built-ins[/]
                      [bold]cd[/] [dir]          change directory
                      [bold]pwd[/]               print working directory
                      [bold]echo[/] [-n] [args]  write to stdout
                      [bold]export[/] NAME=val   set environment variable
                      [bold]env[/]               list environment variables
                      [bold]history[/]           show command history
                      [bold]help[/]              show this help
                      [bold]exit[/] [code]       exit the shell

                    Everything else is run as an external command.
                    Pipes [bold]|[/], redirects [bold]< > >>[/] are supported.
                    """);
                return 0;
        }

        // ── External process ──────────────────────────────────────────────────
        var psi = new ProcessStartInfo
        {
            FileName         = name,
            WorkingDirectory = cwd,
            UseShellExecute  = false,
            RedirectStandardInput  = cmd.Stdin  is not null || stdin  != Console.In,
            RedirectStandardOutput = cmd.Stdout is not null || stdout != Console.Out,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch { Err($"{name}: command not found"); return 127; }

        var tasks = new List<Task>();

        if (psi.RedirectStandardInput)
        {
            tasks.Add(Task.Run(async () =>
            {
                var src = cmd.Stdin is not null ? (TextReader)new StreamReader(cmd.Stdin) : stdin;
                await src.BaseOrSelf().CopyToAsync(proc.StandardInput.BaseStream);
                proc.StandardInput.Close();
            }));
        }

        if (psi.RedirectStandardOutput)
        {
            tasks.Add(Task.Run(async () =>
            {
                Stream dest = cmd.Stdout is not null
                    ? new FileStream(cmd.Stdout, cmd.Append ? FileMode.Append : FileMode.Create, FileAccess.Write)
                    : stdout.BaseOrSelf();
                await proc.StandardOutput.BaseStream.CopyToAsync(dest);
                if (cmd.Stdout is not null) dest.Close();
            }));
        }

        await proc.WaitForExitAsync();
        await Task.WhenAll(tasks);
        return proc.ExitCode;
    }

    static (Stream read, Stream write) Pipe()
    {
        var buf = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var r   = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In,
                      buf.ClientSafePipeHandle);
        return (r, buf);
    }

    static void Err(string msg) =>
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(msg)}");
}

// Helper: get base stream from TextReader/TextWriter or wrap in a MemoryStream proxy
static class StreamHelper
{
    public static Stream BaseOrSelf(this TextReader r) =>
        r is StreamReader sr ? sr.BaseStream : Console.OpenStandardInput();

    public static Stream BaseOrSelf(this TextWriter w) =>
        w is StreamWriter sw ? sw.BaseStream : Console.OpenStandardOutput();
}
