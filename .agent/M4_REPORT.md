# M4 report — Prompts and notifications (partial)

Date: 2026-09-11

## Built

- `PromptCoordinator` on push: `prompt.new` → toast + prompt window; `prompt.closed` closes window; `alert` → toast only (no modal)
- `PromptWindow`: blocks with display degradation, choices by kind, text choice + Enter, select/confirm/`back` selectionId rules
- `NotificationService` via `AppNotificationBuilder` (register best-effort for unpackaged)
- Wired from `App` + `AppSession.EventReceived`

## Gaps / unproven

- Live `agent-sim.js` acceptance not run
- Unpackaged toast AUMID may fail silently (falls back to opening window when push arrives while app runs)
- Pending free-form poll loop is minimal (single refetch)

Build + 43 unit tests green after compliance-wake fixes.
