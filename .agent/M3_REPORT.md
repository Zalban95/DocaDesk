# M3 report — Push loop

Date: 2026-09-11

## Built

- `PushEngine` in `DocaDesk.Core`: SSE via `HttpCompletionOption.ResponseHeadersRead`, infinite timeout on stream client, `: ping` / any traffic resets watchdog, **2 × heartbeatSec** expiry reconnects
- Backoff from `capabilities.push.backoff` (initial/max/factor/jitter) with sane defaults
- Cursor via `FileCursorStore` (persists across restart); gaps tolerated; acks when `ack: true`
- `hello` (resync + heartbeat), `close`/`revoked`, unknown event types ignored without crash
- JSON poll fallback on the **same** cursor when SSE fails
- Wired into `AppSession`: starts when Paired, stops on Offline/Revoked/Unpair

## Tests

43 passed — includes SSE hello+event+ack against `HttpListener`, poll cursor continuity, watchdog configure.

## Live acceptance (manual)

Kill mid-stream / force resync against the live host still needs a paired device. Unit coverage exercises the protocol shapes.

## Next

M4 — prompts and notifications.
