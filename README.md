# Simpleishell

A simple shell application written in C#.

## Project Structure

- **src/App/** - C# shell application source code
  - `App.csproj` - Project file
  - `Executor.cs` - Command execution logic
  - `Lexer.cs` - Tokenization and parsing
  - `Program.cs` - Entry point

## Building

```bash
dotnet build
```

## Running

```bash
dotnet run --project src/App/App.csproj
```

## Features

- Command parsing and lexical analysis
- Command execution
- Shell REPL interface

## Development

This project uses C# with .NET framework. Ensure you have .NET SDK installed.

### Build & Test

```bash
dotnet build
dotnet run
```

## License

See repository for license information.
