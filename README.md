# DocaDesk

The Windows desktop client for a [Doca](../doca/DOCA) server — the "OpenClaw Dashboard", a
Node/Express app that exposes a device-agnostic client API at `/api/v1`.

Two jobs, and the second is the reason it exists:

1. **Show the dashboard.** A WinUI 3 shell hosting the real dashboard in WebView2, plus a tray
   icon, native notifications, and a prompt window that appears whether or not the dashboard is
   open. The dashboard is already a web app; reimplementing it in XAML would be a second
   codebase for the same screens.
2. **Be the agent's hands on this desktop.** DocaDesk hosts its own MCP server over HTTP on the
   tailnet. The human points the dashboard at it, and from then on the Doca harness — running on
   the Linux host — has tools that read and act on *this* machine: window lists, screenshots,
   clipboard, and whatever else gets enabled. Nothing else in the fleet can do that.

Everything the agent can reach here is **off until switched on, visible while on, and killable
in one click.**

## Status

Scaffold stage. The specification is `.agent/DOCA_DESK_BRIEF.md` — read it before writing code.

## Building

Requires the .NET 9 SDK and the Windows App SDK workload. See the brief §3 for the project
layout and the platform baseline; the solution is created as part of milestone M1.

```
dotnet build
dotnet test
```

## Related repos

| Repo | What |
|---|---|
| `../doca/DOCA` | The server: protocol, dashboard, harness, MCP registry |
| `../DocaMobile` | Android phone client (also hosts the dashboard in a WebView) |
| `../DocaWear` | Wear OS client |
