# DOCA 2.10.0 MCP registration addendum — report

Implements `.agent/DOCA_DESK_ADDENDUM_MCP.md` against `PROTOCOL.md` §22.
Brief still governs tools, consent, capture, and listener security (tailnet bind + path secret + remote check).

## What shipped

| Piece | Location |
|---|---|
| Models | `src/DocaDesk.Core/Models/Mcp.cs` |
| Client | `DocaClient.GetMcpSelfAsync` / `PatchMcpSelfAsync` / `OfferMcpAsync` |
| Bearer DPAPI key | `CredentialKeys.McpBearerToken` |
| Listener bearer (404 when missing/wrong, only if enforced) | `McpHttpListener` |
| Reconcile + `mcp.listener` + wait poll | `src/DocaDesk/Services/McpHost.cs` |
| UI waiting / registered copy | Settings MCP block in `MainWindow` |
| Tests | `McpSelfClientTests` (+ existing MCP listener tests) |

Build: WinUI x64 Debug green. Suite: **56** passed.

## §5 answers

### Reconciliation — one place or several?

**One place:** `McpHost.ReconcileRegistrationAsync`.

Called from:

- `StartAsync` (every successful start) with `offerOn404: true`
- `mcp.listener` `start` when already running (URL/header drift)
- a light **wait poll** (≈8s) while `WaitingForAccept`, with `offerOn404: false` so we do not spam `POST /offer`

`RegenerateSecretAsync` does not duplicate reconcile logic: if the listener was running it restarts → `StartAsync` → same reconcile.

### Bearer — enforced or offered-only?

**Offered always** on offer/PATCH as `headers.Authorization: Bearer <token>` (DPAPI-stored).

**Enforced after** `GET`/`PATCH` response shows an `Authorization` key on the host record; then `SetEnforceBearer(true)`. Until then the listener still accepts path+remote without the header so we do not lock the host out of a working entry.

Not yet done (optional hardening): wait for a successful host *connection* before enforcing; we treat the host record as the signal. Wrong/missing bearer when enforced → same **404** as a wrong path. Tokens are not logged (`RedactingLogger` covers `Bearer …`).

### PROTOCOL §22 — wrong / unclear / missing?

Nothing blocking. Notes:

1. **No push when an offer is accepted** — only durable `mcp.listener` for start/stop. We poll `GET /mcp/self` while waiting so the UI can flip to Registered without a restart.
2. **`headers` on GET** — we assume keys (at least `Authorization`) are visible after accept/PATCH. If the server ever redacts header *names*, enforcement would never arm; worth confirming on a live 2.10 host.
3. **`PATCH` ignoring `command`/`transport`/`autostart`** — asserted in unit tests (fields may appear on the wire; server must ignore). Production patch body only sends `url` + `headers`.
4. **§22 “refuse if consent off”** — we refuse `mcp.listener` start when the **master switch** (`McpAutoStart`) is off *or* no tool has consent. Host `stop` stops the listener but does **not** clear the master switch.

## Acceptance checklist (addendum §4)

| # | Status |
|---|---|
| 1 Offer on start when no self → UI waiting | Implemented (needs live 2.10 server to confirm) |
| 2 Accept → GET self 200 | Poll + reconcile (live) |
| 3 Regen + start → PATCH new URL | Implemented |
| 4 `mcp.listener` start/stop + refuse when master off | Implemented |
| 5 PATCH `command` ignored | Unit-tested (client sends; server behaviour is host-side) |
| 6 Harness `list_windows` under paired-client section | Needs live 2.10 |

Live acceptance against a real **2.10.0** server: see `.agent/LIVE_ACCEPTANCE_2_10_MCP.md` (2026-09-12). Prep required adding `mcp:self` to the pre-2.10 device scopes.
