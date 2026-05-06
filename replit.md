# .NET Dev Environment

A clean .NET 9 development environment with git and neovim configured for C# coding.

## Run & Operate

- `dotnet run --project src/App` — run the console app
- `dotnet build App.sln` — build the solution
- `dotnet test` — run tests (add test projects to the solution as needed)
- `dotnet add package <name>` — add a NuGet package to a project
- `nvim` — open neovim (plugins auto-install on first launch via lazy.nvim)

## Stack

- .NET 9.0 (SDK 9.0.308)
- C# — primary language
- OmniSharp — C# LSP (bundled with the .NET module)
- Neovim 0.11 — editor, configured at `~/.config/nvim/init.lua`
- Git 2.49

## Where things live

- `App.sln` — solution file (root)
- `src/App/` — main console project (`Program.cs`, `App.csproj`)
- `~/.config/nvim/init.lua` — neovim config (lazy.nvim, LSP, Treesitter, Telescope)
- `.editorconfig` — shared formatting rules

## Architecture decisions

- Solution at repo root; source projects under `src/` for a clean layout.
- OmniSharp LSP wired into neovim via `nvim-lspconfig` — autocomplete, go-to-def, rename, and code actions all work out of the box.
- lazy.nvim bootstraps itself on first `nvim` launch — no manual plugin install step needed.
- `.editorconfig` enforces consistent formatting across editors and CI.

## Product

A ready-to-code .NET 9 C# workspace with:
- Full LSP support (autocomplete, diagnostics, go-to-definition, rename, code actions)
- Fuzzy file/grep search via Telescope (`<Space>ff`, `<Space>fg`)
- File tree via Neo-tree (`<Space>e`)
- Git change indicators in the gutter (gitsigns)
- Syntax highlighting via Treesitter (C#, JSON, XML, TOML, Bash, Markdown)

## Neovim key bindings

| Key | Action |
|---|---|
| `<Space>ff` | Find files (Telescope) |
| `<Space>fg` | Live grep (Telescope) |
| `<Space>fb` | Buffers (Telescope) |
| `<Space>e` | Toggle file tree (Neo-tree) |
| `gd` | Go to definition |
| `gr` | Go to references |
| `K` | Hover docs |
| `<Space>rn` | Rename symbol |
| `<Space>ca` | Code actions |
| `<Space>f` | Format file |
| `[d` / `]d` | Previous / next diagnostic |

## Gotchas

- Neovim plugins install automatically on the first `nvim` launch — this takes ~30 seconds with an internet connection. Wait for the install to finish before opening `.cs` files.
- OmniSharp needs a `.sln` or `.csproj` in the project root to activate. The solution file `App.sln` satisfies this.
- Run `dotnet build` at least once before opening files in neovim so OmniSharp can index the project correctly.

## Pointers

- [.NET 9 docs](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/overview)
- [OmniSharp neovim setup](https://github.com/neovim/nvim-lspconfig/blob/master/doc/configs.md#omnisharp)
- [lazy.nvim docs](https://lazy.folke.io/)
