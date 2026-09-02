# Contributing to clircs

clircs is a Windows-native console IRC client written in C#. This guide is for contributors working from source. Start with [ARCHITECTURE.md](ARCHITECTURE.md) before making a change that crosses protocol, session, routing, window, persistence, DCC, protection, or scripting boundaries.

By contributing to clircs, you agree that your contribution is licensed under the GNU General Public License, version 3 or later.

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK
- Visual Studio is optional; the command-line build is authoritative

The SDK version policy is recorded in `global.json`. NuGet dependencies come from the source declared in `NuGet.Config`.

## Build, test, and run

From the repository root:

```powershell
dotnet restore clircs.sln
dotnet build clircs.sln -c Release --no-restore
dotnet run --project tests/Clircs.Core.Tests/Clircs.Core.Tests.csproj -c Release --no-build
dotnet run --project src/Clircs.Console/Clircs.Console.csproj -c Release --no-build
```

The tests are a purpose-built executable suite, so use `dotnet run` rather than `dotnet test`. A restricted Windows environment may skip the Schannel tests that require access to the current user's certificate key store; the suite reports those skips explicitly.

## Source map

| Path | Responsibility |
| --- | --- |
| `src/Clircs.Core` | IRC protocol, session state, commands, protection, DCC models, and domain behavior |
| `src/Clircs.Transport` | TCP, TLS, DCC chat, and DCC file transport |
| `src/Clircs.Infrastructure` | Durable files, profiles, credentials, certificates, and user-directory persistence |
| `src/Clircs.Scripting` | Sandboxed JavaScript runtime and script-owned resources |
| `src/Clircs.Console` | Application coordination, commands, routing, windows, and terminal presentation |
| `tests/Clircs.Core.Tests` | Unit, transcript, integration, security, and regression coverage |
| `examples` | Example themes and scripts |

`ARCHITECTURE.md` provides the detailed ownership map and traces inbound and outbound IRC data through the application.

## Working conventions

1. Create a branch for a meaningful change instead of working directly on `main`.
2. Keep changes focused. Architectural work, features, and cosmetic cleanup should not be mixed casually.
3. Preserve domain ownership. Do not duplicate protocol, routing, persistence, validation, or presentation rules in a convenient caller.
4. Add tests for behavior, invariants, edge cases, and failure modes—not merely for the current implementation shape.
5. Run the complete Release build and test suite before requesting review.
6. Update user documentation when behavior changes. Do not bump the application version for repository-only maintenance unless the maintainer requests it.
7. Do not commit build packages, local clircs data, downloaded reference trees, logs, credentials, certificate bundles, or private keys.

The repository enables nullable reference types, implicit usings, deterministic builds, and warnings as errors through `Directory.Build.props`. Follow the formatting rules in `.editorconfig`; avoid drive-by formatting in files unrelated to the change.

## Commits and pull requests

Use a short, specific commit subject written as an action, for example:

```text
Route channel notices before join synchronization completes
```

A pull request should explain the user-visible or architectural outcome, identify important compatibility considerations, and state which verification was run. Screenshots or short transcripts are useful for terminal-presentation changes.

Security-sensitive findings should be shared directly with the maintainer rather than placed in a public issue or transcript. Never include real IRC, services, SASL, bouncer, or certificate credentials in a test fixture.
