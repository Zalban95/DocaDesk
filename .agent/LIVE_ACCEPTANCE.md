# Live / local acceptance notes

Date: 2026-09-11 (final)

## Production live host (§1.1 :4242)

| Check | Result |
|---|---|
| Pair (honest caps) | **OK** — `dev_ee82362ec12f` / `DocaDesk-live-4242` |
| WinUI running + DPAPI | **OK** |
| agent-sim prompt | **OK** — PromptWindow titles visible via `list_windows` (`GPU 0 at 97 °C…`) |
| Prompt select+confirm | **OK** — `confirmed` |
| MCP WinUI listener (5 tools) | **OK** — Tailscale `:8742` |
| Dashboard origin | **OK** — `Runs on: DocaDesk-live-4242` |
| **Harness conversation** | **OK** — `mcp__docadesk-tools__list_windows` + `__screenshot` → `med_2865fb96a13010ff`, WGC; SSE `done` `s_mtwy0d9u0pagat` |
| Environment block | Names device + “tools act on that machine” |

## Tests

`dotnet test`: **52+ passed** (full suite; capture smoke can flake under concurrent WGC/memory pressure).

## Out of scope

Streaming (§5.7).
