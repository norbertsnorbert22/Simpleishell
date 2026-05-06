using Spectre.Console;

var history  = new List<string>();
var lastCode = 0;

AnsiConsole.MarkupLine("[bold orange1]smpsh[/] [dim]— Simpleishell  •  type [bold]help[/] to get started[/]\n");

while (true)
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var dir  = Executor.Cwd.StartsWith(home) ? "~" + Executor.Cwd[home.Length..] : Executor.Cwd;

    AnsiConsole.Markup($"[bold cyan]{Environment.UserName}[/][dim]:[/] [bold blue]{Markup.Escape(dir)}[/][dim] %[/] ");

    var line = Console.ReadLine();
    if (line is null) { AnsiConsole.WriteLine(); break; }
    line = line.Trim();
    if (line == "") continue;
    history.Add(line);

    List<Statement> stmts;
    try   { stmts = Lexer.Parse(line); }
    catch (Exception ex) { AnsiConsole.MarkupLine($"[red]parse error:[/] {Markup.Escape(ex.Message)}"); lastCode = 1; continue; }

    lastCode = Executor.Run(stmts);
}
