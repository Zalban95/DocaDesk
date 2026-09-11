# DocaDesk M1–M7 completion report

Date: 2026-09-11

## What was built

WinUI 3 unpackaged client (`DocaDesk`) with Core / Capture / Mcp / Tests:

- Pairing, cert pin, WebView2 dashboard (no auth header / no JS bridge), tray, single-instance
- Push SSE + poll, watchdog, cursor, resync, acks
- Native prompts + notifications; metrics panel with `If-None-Match`
- Windows.Graphics.Capture (+ PrintWindow / GDI fallbacks); media upload; audit
- MCP HTTP listener (Tailscale), five tools, consent, settings URL; battery sensor when present

## Versions used

| Component | Version |
|---|---|
| .NET | 9.0.318 (`global.json`) |
| Windows App SDK | 1.7.260224002 |
| WebView2 | 1.0.4191.47 |
| H.NotifyIcon.WinUI | 2.3.0 |
| Vortice.Direct3D11 / DXGI | 3.6.2 |
| System.Drawing.Common | 9.0.9 |

## Deviations / notes

1. Zoom via CSS (WinUI WebView2 has no `ZoomFactor`) — not a JS bridge.
2. MCP uses hand-rolled `SimpleHttpServer` (avoids HttpListener URL ACL).
3. Protocol forbids device self-revoke; Unpair clears local token only.
4. Local Windows excludes TCP 4144–4243 → acceptance also used `:9442` before production `:4242` worked.
5. Acceptance tester registered MCP via dashboard `/api/mcp` (allowed for humans); the **app** does not call that API.

## Live acceptance (§1.1)

Documented in `.agent/LIVE_ACCEPTANCE.md`: production pair, PromptWindow shown, harness chat invoked both MCP tools with correct `origin`, environment block names `DocaDesk-live-4242`.

**Server compatibility: Doca `2.9.0`.**

## Tests

xUnit covers §8: stubs, cert matrix, watchdog (fake clock), resync, MCP↔`client.js`, auth 404/403, selector, error map, etc.
