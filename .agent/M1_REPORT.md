# M1 report — Foundation

Date: 2026-09-11

## Built

- Solution `DocaDesk.sln` with:
  - `src/DocaDesk` — WinUI 3 unpackaged shell (single-instance via `AppInstance`, placeholder window)
  - `src/DocaDesk.Core` — protocol models, `DocaClient`, DPAPI credential store, error mapper, SSE parser, cursor store, heartbeat watchdog (injectable clock), certificate policy, redacting logger
  - `src/DocaDesk.Mcp` — placeholder (M6)
  - `src/DocaDesk.Capture` — capture path selector behind `ICaptureBackend` (M5)
  - `tests/DocaDesk.Tests` — xUnit, 38 tests, all green

## Versions actually used (§3.2 verification)

| Item | Version | Notes |
|---|---|---|
| .NET SDK | **9.0.318** | Installed during M1 (machine previously had only 8.0.200) |
| C# / LangVersion | 13.0 via `Directory.Build.props` | |
| Target | `net9.0` (Core/Mcp), `net9.0-windows10.0.19041.0` (app/Capture/tests) | Min OS 10.0.17763 (1809) |
| Microsoft.WindowsAppSDK | **1.7.260224002** | Latest 1.7 line; NuGet also publishes 2.x — stayed on 1.7 for WinUI 3 stability |
| Microsoft.Windows.SDK.BuildTools | **10.0.26100.1742** | |
| Microsoft.Web.WebView2 | **1.0.4191.47** | Evergreen runtime still required on the machine |
| H.NotifyIcon.WinUI | **2.3.0** | **2.4.1 requires net10** — incompatible with net9; reported as §3.2 drift |

## Deviations / ambiguities

1. **Credential Manager vs DPAPI.** Brief prefers Windows Credential Manager *or* DPAPI. M1 ships DPAPI `CurrentUser` under `%LOCALAPPDATA%\DocaDesk\credentials\` (`DpapiCredentialStore`). Can swap to CredMan in M2 if you prefer the named vault UX.
2. **H.NotifyIcon 2.4.x needs .NET 10.** Kept 2.3.0. Ask before retargeting the app to net10.
3. **Windows App SDK 2.x exists** on NuGet; deliberately not used yet.
4. **Mcp / Capture** are stubs only — expected for M1; real work is M5/M6.
5. **Tray / WebView / pairing UI** not in M1 (M2). Single-instance redirect is wired in `Program.cs`.

## PROTOCOL vs brief

No conflict found while implementing Core error codes and pairing/capabilities/events shapes. Followed PROTOCOL error table + Mobile’s sealed-error style.

## Tests (§8 Core subset)

- Model leniency (unknown fields / event types / block display)
- Full error-mapping theory table
- Cursor file persistence + gap-tolerant advance
- Heartbeat watchdog with `FakeClock` (no 50s wall wait)
- `selectionId` idempotency + new-id-after-back
- Credential round-trip (memory + DPAPI on Windows)
- Log redaction (token, MCP path, clipboard)
- Certificate matrix (5 cases)
- Capture path selection

Not yet (later milestones): MCP vs `modules/mcp/client.js`, listener auth, live `agent-sim.js` E2E.

## Server-side gap (already in brief)

Dashboard MCP form still has no headers field — still worth adding for bearer auth instead of URL path secrets.

## Ask before M2

Ready for **M2 — Pair and show** when you sign off on this report.
