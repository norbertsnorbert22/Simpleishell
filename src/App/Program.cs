using Spectre.Console;

// ── Shell state ───────────────────────────────────────────────────────────────
var cwd      = Directory.GetCurrentDirectory();
var history  = new List<string>();
var lastCode = 0;

AnsiConsole.MarkupLine("[bold chocolate1]mysh[/]  [dim].NET 9 — type [bold]help[/] to get started[/]\n");

// ── REPL ──────────────────────────────────────────────────────────────────────
while (true)
{
    var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var dir    = cwd.StartsWith(home) ? "~" + cwd[home.Length..] : cwd;
    var status = lastCode == 0 ? "[green]❯[/]" : $"[red]❯[/]";

    AnsiConsole.Markup($"[bold yellow]{dir}[/] {status} ");

    var line = Console.ReadLine();
    if (line is null) { AnsiConsole.WriteLine(); break; }   // Ctrl+D
    line = line.Trim();
    if (line == "") continue;
    history.Add(line);

    List<Cmd> pipeline;
    try   { pipeline = Lexer.Parse(line, cwd); }
    catch (Exception ex) { Err(ex.Message); lastCode = 1; continue; }

    lastCode = await Executor.RunAsync(pipeline, history, ref cwd);
}

static void Err(string msg) =>
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(msg)}");
