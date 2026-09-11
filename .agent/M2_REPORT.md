# M2 report — Pair and show

Date: 2026-09-11

## Built

- **Pairing UI** with server URL, device name, pair code, optional cert pin, fingerprint probe
- **`AppSession`**: DPAPI token/URL/pin, `Pair` / `Unpair` / `RefreshConnection`, states Unpaired / Offline / Revoked / Paired
- **Honest caps** via `DeviceCapsFactory` (`formFactor: desktop`, real screen, no fake sensors/camera/voice)
- **WebView2 dashboard** at server root — no Authorization header, no script-injected token, no `WebMessageReceived` bridge
- External navigations / `target=_blank` open in the system browser
- **Offline / revoked** first-class panels (not Chromium error page)
- **Tray** (`H.NotifyIcon.WinUI`): close hides to tray; Quit is explicit; `--tray` / autostart HKCU Run
- **Single instance** already in `Program.cs`; redirected activation shows the first window
- Zoom (+/−/100%) via CSS zoom (WinUI WebView2 has no `ZoomFactor`); Reload button

## Versions (unchanged from M1 unless noted)

| Item | Version |
|---|---|
| .NET SDK | 9.0.318 |
| Windows App SDK | 1.7.260224002 |
| WebView2 package | 1.0.4191.47 |
| H.NotifyIcon.WinUI | 2.3.0 |
| System.Drawing.Common | 9.0.9 (tray icon generation) |

## Deviations

1. **Zoom** uses `document.documentElement.style.zoom` because WinUI `WebView2`/`CoreWebView2` expose no `ZoomFactor` in this SDK. Not a JS bridge (no host objects / `WebMessageReceived`).
2. **Live acceptance** (pair against tailnet host, revoke from dashboard) needs a human with a pair code — not automated in this report. Build + unit tests are green.
3. **Tray icon** is generated at runtime into `%LOCALAPPDATA%\DocaDesk\tray.ico` rather than a checked-in asset.

## Tests

`dotnet test`: **40 passed** (includes `DeviceCapsFactory` honesty checks).

## Ask before M3

Ready for **M3 — Push loop** when you sign off. Continuing Core push implementation in parallel under the active goal if you prefer not to block.
