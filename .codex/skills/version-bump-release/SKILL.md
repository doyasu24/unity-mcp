---
name: version-bump-release
description: >-
  Use when preparing a new release version for unity-mcp.
  Covers manual version-bump work for both the dotnet tool (Server)
  and the Unity Plugin (UPM package).
  Do NOT use for CI-automated release tasks.
---

# version-bump-release

## Purpose
Use this skill when preparing a new release version for `Doyasu24.UnityMcp.Tool`.
This skill covers only manual version-bump work. CI-automated tasks are excluded.

## Inputs
- `NEXT_VERSION` (SemVer, without `v`, e.g. `0.2.0`)

## Manual Checklist
1. Update the tool package version:
   - `Server/UnityMcpServer.csproj` `<Version>`
2. Update the server runtime version constant:
   - `Server/Common.cs` `Constants.ServerVersion`
3. Update user-facing version references in `README.md`:
   - `dotnet tool install --local ... --version ...`
   - Unity package URL tag (`#v...`)
4. Update the Unity Plugin package version:
   - `UnityMCPPlugin/Assets/Plugins/UnityMCP/package.json` `version`

## Verification
1. Confirm all required version references are aligned with `NEXT_VERSION`.
2. Confirm the Unity Plugin `package.json` version matches `NEXT_VERSION`.
3. Run a local smoke check (recommended):
   ```bash
   dotnet pack Server/UnityMcpServer.csproj -c Release -o ./artifacts
   rm -rf /tmp/unity-mcp-tool-test
   dotnet tool install --tool-path /tmp/unity-mcp-tool-test --add-source ./artifacts Doyasu24.UnityMcp.Tool --version <NEXT_VERSION>
   /tmp/unity-mcp-tool-test/unity-mcp --port 0
   ```
   - Expected: tool installs successfully, and `--port 0` returns config validation error.

## Release Trigger
- Create and push Git tag `v<NEXT_VERSION>` after manual version bump is complete.
- Tag push starts the release workflow.
