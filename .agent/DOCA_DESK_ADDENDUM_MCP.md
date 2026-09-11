# Addendum: registering and maintaining the MCP listener (DOCA 2.10.0)

**Status:** addendum to `DOCA_DESK_BRIEF.md`. The brief is unchanged and still
governs everything else. This file covers one thing the brief could not: at the
time it was written the server had no client-facing MCP surface at all, so the
brief told you to have a human paste your URL into the dashboard by hand. DOCA
2.10.0 adds the endpoints that make that unnecessary. Where this file and the
brief differ **on MCP registration specifically**, this file is current.

Nothing here changes the tool set, the consent model, the capture rules, or the
security posture of your listener. Those parts of the brief stand as written.

---

## 1. What the server gained

A new scope family, `mcp:self`, included in the `phone` preset you already pair
with — so a device paired after 2.10.0 has it without anyone editing scopes.

```http
GET   /api/v1/mcp/self     → 200 { server: { id, label, transport, url, headers, autostart, state, error, toolCount } }
                             404 when the host has no server pointed at this device
PATCH /api/v1/mcp/self     → 200 { server }     body: { url?, headers? }
POST  /api/v1/mcp/offer    → 202 { offer }      body: { label?, url, headers?, tools?, note? }
```

Plus a push event on your existing `/events` stream:

```
mcp.listener   { action: "start" | "stop", serverId, url, by }
```

And in the dashboard: MCP servers are now shown in two labelled sections, "On
the DOCA host" and "On a paired client", so your listener is visibly a different
kind of thing from a server the host spawns. Each of your tools also reaches the
model with a sentence naming the machine it acts on, which is what lets a user
say "screenshot my PC" and get you rather than the host's own framebuffer. You
do not have to do anything for that — it is derived from the definition's origin
— but it is why the `label` you offer matters: it becomes the machine name the
model sees.

`PROTOCOL.md` §22 is the normative text. Read it before implementing.

## 2. What you must not try

The registry is not yours to write, and this has not been relaxed. `mcpServers`
holds an address the host calls and, for a host-side server, a command it
spawns — and the legacy `POST /api/mcp` is unauthenticated to every peer on the
tailnet. So:

- You **cannot** create a definition. `POST /api/v1/mcp/offer` asks; a human
  click in the dashboard is what creates it. `202` means recorded, not accepted.
- You **cannot** delete or start one, and there is no endpoint that pretends to.
- `PATCH /mcp/self` writes the **address only**. Sending `command`, `transport`,
  `autostart` or a different `origin.deviceId` is not an error — those fields are
  ignored. Do not build anything that depends on them landing.
- There is no admin form of `/mcp/self`. You can see your own entry and no other.

Treat all of this as the design working, not as a limitation to route around.

## 3. What to implement

### 3.1 Announce the listener when it starts

`McpHost.StartAsync()` succeeds and `_listener.BoundUrl` is now the live URL. At
that point reconcile with the host, in this order:

1. `GET /api/v1/mcp/self`.
2. **404** — the host has no entry for you. `POST /api/v1/mcp/offer` with
   `label` (something a person will recognise on a card, e.g. the machine name),
   `url` = `BoundUrl`, `tools` = `McpHost.ToolNames`, and a short `note`. Then
   stop: you are waiting on a human. Surface that state in the UI as *waiting to
   be accepted in the DOCA dashboard*, not as an error.
3. **200 with a different `url`** — the host is holding a stale address.
   `PATCH /api/v1/mcp/self { url }`.
4. **200 with the same `url`** — nothing to do.

Do this on every successful start, not once at first run. It is the whole point:
the reconciliation is cheap and it is what stops a dead entry surviving a change
of address.

### 3.2 Re-announce whenever the address changes

`RegenerateSecretAsync()` changes the path, and the tailnet IP in `BoundUrl` can
change without you doing anything. Both cases are a `PATCH /mcp/self` (or an
offer, if you have never been accepted). Route them through the same
reconciliation as §3.1 rather than duplicating the logic.

### 3.3 Handle `mcp.listener`

Your `PushEngine` already dispatches typed events; add this one.

- `action: "start"` → start the listener, then run the §3.1 reconciliation. The
  `url` in the payload is what the host currently holds, so a mismatch is your
  cue to correct it.
- `action: "stop"` → stop the listener.

**It is a request, not a command.** If the user has switched the listener off, or
has consent off for everything, refuse it and say so in your own UI. The host
does not get a reply channel for this and does not need one: it learns you
started by connecting to you. Never let this event override a user's explicit
off.

The event is durable for five minutes only, so one that arrives after a long
disconnect is stale by design — treat it the same as any other event you receive,
but do not go looking for missed ones.

### 3.4 Use `headers` now that you have it

The brief noted, correctly, that the dashboard's add-server form had no field for
request headers, and told you to secure the listener with three things instead: a
tailnet-only bind, a long random path segment, and a remote-address check. **Keep
all three.** But `headers` is now settable by you, through both `offer` and
`PATCH /mcp/self`, so you can additionally require a bearer token:

- Generate a listener token, store it beside the path secret in
  `CredentialKeys` (DPAPI, same as now).
- Send it as `headers: { "Authorization": "Bearer <token>" }` when you offer or
  patch.
- Reject requests without it in `McpHttpListener`, with the same 404-shaped
  response you use for a wrong path — do not confirm to a prober that the path
  was right.

Add the check only after the header is known to be reaching you, or you will lock
the host out of a listener it had working. The safe order is: patch the header
first, confirm the host still connects, then enforce.

Do not log the token. `RedactingLogger` already covers `Authorization`; confirm
it covers this path too.

## 4. Acceptance

Testable without the dashboard, against a real server:

1. With no entry on the host, starting the listener produces a pending offer.
   `GET /api/v1/mcp/self` is still 404. Your UI says you are waiting for someone.
2. Accepting the offer in the dashboard creates an http server whose origin is
   this device. `GET /api/v1/mcp/self` now answers 200.
3. `RegenerateSecretAsync()` followed by a start leaves `GET /mcp/self` showing
   the **new** URL, with no human involvement.
4. A `mcp.listener` `start` while the listener is off starts it. The same event
   with the user's master switch off does **not** start it, and says why locally.
5. `PATCH /api/v1/mcp/self` with a `command` field changes nothing but the
   address. Assert this rather than assuming it — it is the property the whole
   design rests on.
6. With the listener running and accepted, the harness can call
   `mcp__<your-id>__list_windows` and gets your windows, and the dashboard shows
   you under "On a paired client".

## 5. Report back

In your milestone report, say:

- Whether the reconciliation in §3.1 ended up in one place or several, and if
  several, why.
- Whether the bearer token in §3.4 is enforced, or offered-but-not-enforced, and
  what is left to do.
- Anything in `PROTOCOL.md` §22 that turned out to be wrong, unclear, or missing
  a field you needed. That section is new and has one implementor; if something
  does not fit, say so plainly rather than working around it silently.
