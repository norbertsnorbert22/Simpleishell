# smpsh — Simpleishell

A Unix-style shell written in C# with a custom natural-language-flavoured syntax.

## Run & Operate

- `dotnet run --project src/App` — launch the shell
- `dotnet build App.sln` — build
- `dotnet test` — run tests (add test projects as needed)

## Stack

- .NET 9.0 (SDK 9.0.308)
- C# — primary language
- Spectre.Console 0.55.2 — coloured prompt + markup
- Neovim 0.11 with lazy.nvim, OmniSharp LSP, Telescope, Treesitter, chocolatier theme

## Where things live

- `App.sln` — solution file (root)
- `src/App/Program.cs` — REPL loop + coloured prompt
- `src/App/Lexer.cs` — tokeniser: parses `, also,` / `>>` / `!args`
- `src/App/Executor.cs` — command dispatcher + builtins
- `src/App/App.csproj` — project + NuGet refs
- `~/.config/nvim/init.lua` → `/home/runner/workspace/.config/nvim/init.lua` — neovim entry point

## smpsh syntax

| Operator | Meaning |
|----------|---------|
| `>>`     | Pipe output to next command |
| `, also,`| AND — run next only if previous succeeded |
| `;`      | Sequential — always run next |

**Argument format:**
- `!flag` / `!abc` — single or stacked flags
- `!key=value` — named variable
- `!name(a, b)` — list

## Commands

`hi`, `say`, `list`, `copy`, `cd`, `pwd`, `help`, `exit`

See spec: `attached_assets/Pasted--Simpleishell-…`

## Architecture decisions

- Three files: `Lexer.cs` owns all parsing, `Executor.cs` owns all command logic, `Program.cs` is only the REPL.
- Lexer produces a `List<Statement>` where each `Statement` holds a pipe-chain (`List<Cmd>`) and a join operator (`Also | Seq | End`).
- Pipeline output is buffered in a `StringBuilder` and threaded from one command's stdout to the next's stdin.
- Neovim stdpath quirk on Replit: `stdpath('config')` = `/home/runner/workspace/.config/nvim` — NOT `~/.config/nvim`.

## User preferences

- 2–5 source files — not 1 monolith, not 20 micro-files.
- Keep things concise; no over-engineering.
- Chocolatier colorscheme in neovim.

## Gotchas

- Spectre.Console does NOT support `chocolate1` as a color name — use `orange1` or other standard names.
- Neovim plugins auto-install on first launch (~30 s). Run `dotnet build` before opening `.cs` files so OmniSharp indexes correctly.
- `dotnet run` from any directory works because the `--project` flag is explicit.

## Pointers

- [smpsh spec](attached_assets/Pasted--Simpleishell-…txt)
- [.NET 9 docs](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/overview)
- [Spectre.Console markup](https://spectreconsole.net/markup)
