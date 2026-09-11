# M4–M7 reports

Date: 2026-09-11

## M4 — Prompts and notifications

- `PromptCoordinator` + `PromptWindow` + `NotificationService`
- Alerts → toast only; prompts → toast + window; `prompt.closed` dismisses
- Text choices with Enter; selectionId / back / conflict handling
- **Unproven live:** `agent-sim.js` against paired device; unpackaged AUMID toasts may no-op (window still opens from push)

## M5 — Screenshots

- `ScreenCapturer`: monitor/window via **Windows.Graphics.Capture** (free-threaded frame pool + Vortice D3D11 readback) when the device can be created; **PrintWindow** / `CopyFromScreen` as fallbacks
- `GraphicsCaptureSession.IsSupported()` + lazy `GraphicsCapturePipelineReady`; yellow capture border left alone (§7.4)
- Downscale long-edge 1600; PNG for windows, JPEG for monitors / oversized PNG
- Upload via `POST /api/v1/media`; tool returns **text** media id/url (not image content)
- `AuditLog` + tray tooltip flash on capture
- Tests: grabber `TryCreate` + monitor capture smoke when WGC is available

## M6 — MCP server

- Hand-rolled HTTP JSON-RPC (`SimpleHttpServer`, no SDK) matching Doca `modules/mcp/client.js`
- Bind Tailscale IPv4 only (loopback helper for tests); `/mcp/<secret>` path; remote 100.x / allowed host
- Tools: `list_windows`, `screenshot`, `get_clipboard_text`, `set_clipboard_text`, `open_url` with per-tool consent (all off by default)
- Settings: listener toggle, copyable URL, regenerate secret, registration instructions, audit list
- Tests: secret-path 404; initialize/tools/list; **Doca `McpClient` node handshake** against loopback listener
- **Server gap (flagged again):** dashboard MCP form still lacks headers field

## M7 — Metrics + settings polish

- Settings distinguishes device settings vs server profiles (copy in UI)
- Consent switches prominent; autostart; unpair
- `MetricsPanelWindow`: profile poll with `If-None-Match`, renders by metric **kind** (not hard-coded ids)

## Still incomplete vs full brief acceptance

- Live pair / revoke / agent-sim / harness `list_windows`+`screenshot` on real conversation (needs pair code for `:4242` or manual WinUI against `:9442`)
- Protocol: device cannot self-revoke — Unpair clears local token; dashboard revoke is the server-side path
- Streaming (§5.7) intentionally not done

`dotnet test`: **51+** passed (incl. WGC smoke). App build (x64 unpackaged) succeeds.
