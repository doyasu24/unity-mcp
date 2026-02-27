# AGENTS.md

## Documentation Policy
- `README.md` is end-user documentation only.
- Do not add developer/maintainer procedures to `README.md`.
- Place developer documentation under `docs/` or as skills.
- Do not document steps that are already automated by GitHub Actions workflows.

## Quality Gates
- After making code changes in `Server` or `Server.Tests`, run:
  - `dotnet test Server.Tests/UnityMcpServer.Tests.csproj`
- After making code changes in `Server` or `Server.Tests`, run formatting:
  - `dotnet format Server/UnityMcpServer.csproj`
  - `dotnet format Server.Tests/UnityMcpServer.Tests.csproj`
- If tests or formatting cannot be run, report the reason and the exact command that failed.
