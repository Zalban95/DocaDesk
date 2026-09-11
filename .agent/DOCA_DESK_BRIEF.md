# DocaDesk — Implementation Brief

**Read this whole file before writing code.** It is the authoritative description of what
`DocaDesk` must become. Where this file and your own instincts disagree, this file wins. Where
this file and `PROTOCOL.md` on the server disagree, **`PROTOCOL.md` wins** — tell me about the
conflict instead of guessing.

---

## 0. Mission in one paragraph

`DocaDesk` is the **Windows desktop client** for a Doca server (the "OpenClaw Dashboard", a
Node/Express app that exposes a device-agnostic client API at `/api/v1`). It has two jobs, and
they are not equally important. The first is to *show the dashboard*: a WinUI 3 shell hosting the
real dashboard in WebView2, with a tray icon, native notifications and a prompt window. The
second is the reason the app exists at all.

The single most important sentence in this document: **DocaDesk is where the Doca agent reaches a
Windows desktop it could otherwise not touch.** The harness runs on a Linux host. It can read
that host's files, run that host's shell, see that host's containers. It cannot see this screen,
this clipboard, or this copy of Excel. DocaDesk closes that gap by hosting **its own MCP server**
on the tailnet and letting the harness call into it. Everything else in this app — the WebView,
the tray, the metrics — is a nicer way to reach a web page that a browser already renders. The
MCP half is not.

Which is why the second most important sentence is: **everything the agent can reach on this
machine is off until the user switches it on, visible while it is on, and killable in one click.**
An app that can quietly hand a remote model a live view of somebody's desktop is not a feature
with a security caveat; it is the caveat.

### 0.1 What this is not

- **Not a reimplementation of the dashboard.** No XAML file manager, no XAML terminal, no XAML
  docker page. The dashboard is a web app served by the same host you are talking to; put it in
  WebView2 and spend your effort on the things a web page cannot do (§5.2, §5.4). DocaMobile made
  this decision first — see its brief §0.1 — and it is the same decision here for the same
  reason.
- **Not a second server.** DocaDesk never manages Doca's own configuration. It does not create
  MCP definitions, does not create or control VMs, does not write prefs. It publishes an endpoint
  and waits to be pointed at it by a human in the dashboard. §2 rule 6 explains why this is a
  hard rule and not a phase-one simplification.
- **Not a place for the harness.** The agent loop, its model keys, its memory and its settings
  proposals live on the Linux host. DocaDesk does not embed a model, does not hold a provider
  key, and does not run an agent loop.
- **Not an unattended remote-access tool.** No hidden service, no auto-accept, no "always allow"
  that survives a restart without the user seeing it. If you find yourself building something
  whose honest description is "remote desktop with extra steps", stop and ask me.
- **Not a phone app in a window.** The phone and watch are designed around glanceability and a
  two-second answer. A desktop has a keyboard, a big screen and a user who is already working;
  design for *not interrupting*, and for the interruption being worth it when it comes.

---

## 1. Where the truth lives

The server repo is on the same machine, beside this one. **Read it rather than guessing**, and
never hard-code anything you could have discovered from `GET /api/v1/capabilities`.

| Source | What it is | Path |
|---|---|---|
| `PROTOCOL.md` | **Normative** spec of the Doca Client Protocol v1.0 | `D:\doca\doca\DOCA\PROTOCOL.md` |
| `docs/api/device-app-guide.md` | The guide written for exactly this kind of app — read it twice | `D:\doca\doca\DOCA\docs\api\` |
| `docs/api/cookbook.md` | Copy-paste starting points per concern | same |
| `docs/api/agent-guide.md` | The *other* side of the wire: what the harness sees and can ask for | same |
| `docs/api/openapi.json` | Machine-readable API, also served live at `GET /api/v1/openapi.json` | same |
| `clients/reference/watch.sh` | **A working reference client.** When unsure what a call should look like, read this first | `D:\doca\doca\DOCA\clients\reference\` |
| `clients/reference/agent-sim.js` | Simulates the agent side, so you can raise prompts at yourself | same |
| `modules/mcp/client.js` | **The exact code that will call your MCP server.** Read it before designing the listener | `D:\doca\doca\DOCA\modules\mcp\` |
| `modules/mcp/registry.js` | Where a server definition lives, including the `origin` field that names this device | same |
| `modules/harness/environment.js` | What the agent is told about you every turn | `D:\doca\doca\DOCA\modules\harness\` |
| `docs/client-briefs.md` | Why this brief is shaped the way it is | `D:\doca\doca\DOCA\docs\` |
| DocaMobile's brief | The phone client, including the WebView decision in its §0.1 | `D:\doca\DocaMobile\.agent\DOCA_MOBILE_BRIEF.md` |
| DocaWear's brief | The watch client; its §4.5 prompt state machine is the one to mirror | `D:\doca\DocaWear\.agent\DOCA_WEAR_BRIEF.md` |

`watch.sh` is a *reference*, not a model of production quality. It deliberately skips things you
must do: it never checks HTTP status, never sends `If-None-Match`, never auto-acks, and has no
heartbeat watchdog. Copy its request shapes, not its error handling.

### 1.1 The live server

There is a real Doca host running on the tailnet:

```
https://al-office-desk.tail08f157.ts.net:4242/
```

It serves a genuine certificate (Tailscale issues one for the `ts.net` name), so a normal
`HttpClient` validates it with no pinning and no exception. Develop against it. A locally run
server self-signs into `.certs/` instead, which is the case §7.2 exists for.

### 1.2 Running a server to develop against

`npm start` in the server repo listens on `0.0.0.0:4242` over HTTPS. Mint yourself a token with
`npm run token -- issue --name devdesk --preset phone`, or — better, because it exercises the real
path — pair from the dashboard at **Settings → API Keys → Pair a device**.

Raise a prompt at yourself with `clients/reference/agent-sim.js`. **Do not build UI for prompts
you have never seen arrive.**

---

## 2. Ground rules that shape every line of code

1. **Discover, never hard-code.** No surface id, metric id, command id, limit, threshold, colour
   or string enum may be baked into the app. `GET /capabilities` and the device profile describe
   what exists; render whatever comes back and ignore what you do not understand.
2. **Forward compatibility is mandatory.** Unknown fields, event types, block types, choice
   kinds, metric kinds and figure kinds must be **ignored without crashing**, and where there is
   a sensible textual degradation, degrade to it. The server adds things without bumping the
   major version. In C# this means `JsonSerializerOptions` that tolerate unknown members and
   enums modelled as strings with an `Unknown` fallback — not `[JsonConverter]`s that throw on an
   unrecognised value, which is the default failure mode of a strongly typed client.
3. **Consent is a correctness property, not a feature.** Every capability that touches the
   desktop — window enumeration, screenshots, clipboard, input, file access — is a switch that
   starts **off**, shows a tray state while **on**, writes an audit line for every use, and can be
   cut with one click. A DocaDesk that works perfectly but can be running invisibly is broken.
   This is this client's equivalent of DocaWear's "battery is a correctness property": the rule
   that outranks convenience every time.
4. **Fail loudly to me, quietly to the user.** No silent mocks, no fake data, no "demo mode"
   fallback when a call fails. If something cannot work, the UI says so in one short line and the
   log says why. The first DocaMobile scaffold shipped mock fallbacks that made a broken
   connection look like a working app; do not repeat it.
5. **Least authority.** This device holds its own token and only its own. It never receives
   another device's token, never proxies for another device, and never acts on another's behalf.
6. **The server is not yours to configure.** DocaDesk never calls the legacy dashboard API to
   change Doca's own state — no `POST /api/mcp`, no `/api/vms`, no prefs writes — even though it
   could. Two reasons, and the second is the real one. The legacy `/api/*` routes are
   **unauthenticated to any tailnet peer**, so nothing would stop you. And `mcpServers` holds *a
   command that gets spawned*: a client that could write there would be an unauthenticated remote
   code execution path on the Doca host. The server repo takes this seriously enough that its own
   agent is forbidden from proposing that key (`modules/harness/settings.js`, and the comment
   above `FORBIDDEN` explains it). Your MCP server gets registered by a human clicking in the
   dashboard. Publishing a URL for them to paste is your whole part in it (§5.5).

---

## 3. Target architecture

### 3.1 Projects

| Project | Contains |
|---|---|
| `src\DocaDesk` | The WinUI 3 app: window, tray, WebView2 host, prompt window, notifications, consent UI, settings |
| `src\DocaDesk.Core` | Protocol client: models, `DocaClient`, token store, push loop and cursor, error mapping. **No UI dependencies** — it must be testable with `dotnet test` on a build agent |
| `src\DocaDesk.Mcp` | The MCP server: HTTP listener, JSON-RPC 2.0 plumbing, the tool registry and the tool implementations |
| `src\DocaDesk.Capture` | Screen and window capture: `Windows.Graphics.Capture`, the `PrintWindow` fallback, encoding and downscaling |
| `tests\DocaDesk.Tests` | xUnit. See §8 |

`DocaDesk.Core` is deliberately a near-twin of DocaMobile's and DocaWear's `:core-doca` modules.
Different language, same shape. **Do not try to share code with them** and do not publish a
package; copying a protocol client per platform is correct, and the transports differ anyway.

`DocaDesk.Mcp` must not reference `DocaDesk` or any WinUI type. The listener has to be startable
from a test with no window, and the day the tools need to run without a UI (a service, a headless
box) that boundary is what makes it possible. `DocaDesk.Capture` is separate for the same reason
plus one more: it is the project most likely to need a different implementation on a future
Windows version, and isolating it keeps that a one-project change.

### 3.2 Platform baseline

**Verify every version against current tooling before you start, and report what you actually
used.** These were right when the brief was written and this ecosystem moves.

- **.NET 9**, C# 13, nullable reference types on and warnings as errors in `DocaDesk.Core`.
- **WinUI 3 via the Windows App SDK.** Not WPF, not WinForms, not UWP. WinUI 3 because it is the
  supported path for a new Windows desktop app, has a first-class WebView2 control, and gives
  modern window styling without a third-party theme.
- **Unpackaged, self-contained deployment** for now: Windows App SDK self-contained plus
  `PublishSingleFile` where it works, so installing is copying a folder and there is no MSIX
  signing dance for a personal tool. Note the cost in your report: some APIs need package
  identity, and if you hit one, say so rather than switching the whole deployment model on your
  own. Do not do the MSIX work speculatively.
- **WebView2** (`Microsoft.Web.WebView2`) with the **Evergreen** runtime, and a real check at
  startup for whether the runtime is installed — a missing WebView2 runtime must produce "install
  the WebView2 runtime, here is the link", not a blank window.
- **Minimum Windows 10 1809**, target Windows 11. `Windows.Graphics.Capture` needs 1803+, and
  hiding the yellow capture border needs `IsBorderRequired` support that only exists on later
  builds — check `GraphicsCaptureSession.IsSupported()` and the border property individually
  rather than assuming a build number, and **do not hide the border by default** even where you
  can (§7.4).
- **`H.NotifyIcon.WinUi`** or equivalent for the tray icon, because WinUI 3 has no built-in one.
  This is on the ask-first list if you want a different package.
- Notifications: `AppNotificationBuilder` from the Windows App SDK. An unpackaged app can still
  raise these; verify and report, because this is exactly the kind of thing that quietly requires
  identity.

### 3.3 Wire details that are easy to get wrong

- **There is no SSE client in the BCL.** Use `HttpClient` with
  `HttpCompletionOption.ResponseHeadersRead`, read the response stream, and parse
  `event:`/`data:` frames yourself. Do not add a third-party SSE library without asking. Set
  `Timeout = InfiniteTimeSpan` on the client used for the stream and enforce liveness with the
  heartbeat watchdog instead — a `HttpClient.Timeout` will otherwise kill a healthy stream.
- **The heartbeat is the only liveness signal.** The server sends `: ping` comments about every
  25 s. No traffic for 50 s means reconnect, whatever the socket thinks. TCP will happily hold a
  dead connection open for far longer than that.
- **The cursor is yours to keep.** Persist the last `seq` you processed and reconnect with
  `?since=`. `seq` may have gaps — do not treat a gap as loss — and a `resync` signal means your
  cursor predates retained history, so refetch state rather than trying to replay.
- **Acks release durable events.** If you never ack, the outbox fills and old events are dropped.
- **`If-None-Match` on the profile.** The server issues ETags and the profile is polled; sending
  the ETag is the difference between a 304 and a full body on every refresh.
- **Idempotent selection.** A prompt selection carries a `selectionId` you generate. Retrying with
  the same id is safe; a new id after a `back` is required. `PROTOCOL.md` §12 is normative and
  DocaWear's brief §4.5 has the state machine already worked out — mirror it rather than deriving
  it again.
- **`SocketsHttpHandler` per concern, not per call.** One long-lived handler, `HttpClient`
  instances sharing it. A `using var client = new HttpClient()` per request exhausts ports, and a
  single client with a finite timeout cannot also hold the stream.

### 3.4 Capabilities this device declares

Declare **honestly**, from the real machine, and never more than you implement.

```
formFactor: "desktop"
screen:     the primary display's real w/h/dpr, shape "rect", color true
input:      { touch: <real>, voice: <only if you implement it>, text: true, camera: <real>, buttons: true }
render:     [ "svg", "svg.smil", "image", "text" ]
motion:     [ "1" ]
exec:       []
sensors:    [ "battery" ] on a laptop, [] on a desktop
```

`formFactor: "desktop"` is a real value in `FORM_FACTORS` (`modules/api-v1/devices.js`) — it was
added for this client. Anything not in that list normalises silently to `"other"`, so if you
invent a value you will not get an error, you will get a wrong device record.

`render` is generous here because a desktop genuinely can draw SVG, unlike the watch. `exec` stays
empty: this app does not run server-supplied code, and that is a §10 non-goal, not a gap.

Do **not** declare sensors you cannot actually sample. A desktop has no heart rate and usually no
location worth reporting; declaring them gets the agent asking for data you will have to refuse.

### 3.5 Scopes to pair with

Pair with the **`phone` preset** as the starting point:

```
read:*  command:*  interact  profile:*  vars:self  sensors:report  media:upload  artifacts:self  devices:admin
```

`media:upload` is the one that matters most for this client — it is how a screenshot reaches the
agent (§5.4). `command:*` is what makes the dashboard's action buttons work inside the WebView.
Do not ask for `*` (admin) or `agent`: this is a device, not the agent, and holding the agent
scope would let it raise prompts at other people's devices.

---

## 4. What already exists

Nothing. This repo is a `.gitignore`, a `README.md` and this brief. You are creating the solution
and every project in it.

That is a licence to get the structure right, not a licence to improvise the protocol: three
clients already speak it, and where DocaMobile or DocaWear solved something, follow them rather
than inventing a third answer.

---

## 5. Feature specification

In build order. Each subsection is what "done" means, not a hint.

### 5.1 Shell, tray and single instance

One window. A tray icon that is the app's real home — closing the window hides it, and quitting is
an explicit tray action, because a dashboard you have to relaunch to see a notification is not
useful.

**Single instance is a requirement, not a polish item.** Two instances means two push streams, two
event cursors racing each other's acks, and two MCP listeners fighting over a port. Use the
Windows App SDK's `AppInstance.FindOrRegisterForKey` / `RedirectActivationToAsync`; a second
launch surfaces the first window and exits.

Autostart is a user setting written to the `Run` key (per-user, `HKCU`) and it starts **to the
tray**, not to a window on the user's screen at login. Never write it without the user asking.

### 5.2 Dashboard in WebView2

Point WebView2 at the server root and let the dashboard be the dashboard.

- **Do not use `AddScriptToExecuteDocumentCreatedAsync` to inject a token, and do not add an
  `Authorization` header to the navigation request.** The legacy dashboard routes the page uses
  are unauthenticated on the tailnet; the page needs no credential, and putting one on the
  navigation is both useless and a way to leak it into a redirect. DocaMobile currently does add
  such a header, contradicting its own brief — that is a bug there, not a pattern to copy.
- **Do not add a JavaScript bridge (`WebMessageReceived` / host objects) in M2.** The page has no
  reason to call into native code yet, and a bridge is the single largest attack surface you could
  add to this app. If you find a case that needs one, that is an ask-first item (§11), and the
  answer will involve a fixed allow-list of message types, not a generic RPC.
- Handle navigation away from the host: a link to an external site opens in the user's real
  browser, not inside the shell.
- Offline is a first-class state (§6.2). A WebView2 network error page is not an acceptable
  offline experience for the app's home screen.
- Zoom, and a reload that actually reloads. Two small things, both immediately missed.

### 5.3 Push loop, notifications and prompts

This is what justifies a native app over a browser tab.

- The push loop from §3.3 runs in `DocaDesk.Core` whenever the app is running, window open or
  not, with the JSON-poll fallback on the same cursor when the stream cannot hold.
- A prompt arriving raises a **native notification** and, if the user acts on it, a small
  **prompt window** — not a navigation inside the dashboard WebView. It has to work with the
  dashboard closed, and it has to render the prompt's blocks and choices from
  `PROTOCOL.md` §12 and §19.1 rather than assuming text.
- Render the block types you can and **degrade unknown ones to their `display` string**, which is
  exactly what that field is for. DocaWear renders only `text` today and that is a shortcut, not
  the design.
- Choices are rendered by `kind`. A desktop has a keyboard: a text choice gets a real text box,
  and Enter submits. This is where the desktop can be genuinely better than the watch.
- Quiet hours, choice filtering and prompt targeting are **server-side**. Do not reimplement them;
  if a prompt reaches you, it is meant for you.
- Alerts are notifications without a reply. Do not turn an alert into a modal.

### 5.4 Screenshots — before streaming

Screenshots first, and get them fully right, because they are most of the value and a fraction of
the work. Streaming is §5.7 and it is deliberately last.

- `Windows.Graphics.Capture` for both full-monitor and per-window capture, with a `PrintWindow`
  fallback for windows the modern API cannot handle (some legacy and some hardware-accelerated
  ones). Check `GraphicsCaptureSession.IsSupported()` at startup, and if neither path works on a
  given window, say which and why — do not return a black frame.
- **`MEDIA_BYTES` is 1.5 MB** (`modules/api-v1/limits.js`). A 4K PNG is not going to fit. Downscale
  to a sensible long edge and pick the encoding deliberately: PNG for text-heavy windows where
  legibility is the point, JPEG for photographic content. Report what you chose and why.
- **How a screenshot reaches the agent is the part to get right, and it is not obvious.** Read
  `modules/mcp/client.js::callTool`: it flattens a tool result's content array to text, mapping
  any non-text part to the literal string `[image]`. **An MCP tool that returns image content
  reaches the model as the four characters `[image]`.** So: upload the PNG with
  `POST /api/v1/media` (that is what `media:upload` is for), and have the tool return **text**
  containing the resulting media id and URL. The harness holds an agent-scoped token and
  `GET /api/v1/media/:id` admits `agent`, so it can fetch what you uploaded. Tell me if you find
  a cleaner path; this one works today with no server change.
- Every capture writes an audit line and flashes the tray state. A screenshot the user cannot tell
  happened is the exact thing §2 rule 3 exists to prevent.

### 5.5 The MCP server

The reason this app exists. Read `modules/mcp/client.js` first — it is hand-rolled, ~250 lines,
and it defines what "an HTTP MCP server" means to Doca more precisely than any spec will.

**What Doca's client actually does:** it POSTs a JSON-RPC 2.0 request to your URL and reads a JSON
response. `initialize`, then `tools/list`, then `tools/call`. It does **not** open a long-lived
event stream, so `notifications/tools/list_changed` is never heard — a tool appearing later is
invisible until the user clicks **↺ Tools** in the dashboard. Design for a stable tool list and
say so in the UI rather than assuming dynamic registration works.

**Do not add an MCP SDK.** Doca deliberately hand-rolled its client rather than take the official
one; a .NET SDK is more defensible here than an ESM package in a CommonJS project, but it is still
an ask-first item, and "answer POST with JSON-RPC" is a small amount of code.

**Registration is a human clicking, and this is not negotiable (§2 rule 6).** What you build:

- A settings page showing the listener's state and its **full URL, with a copy button**.
- Instructions in the UI, in words, for what the user does with it: dashboard → **MCP** tab →
  add a server → **Transport: http** → paste the URL → **Runs on: a paired client** → pick this
  device.
- That last step matters and it is new. `modules/mcp/registry.js` carries an
  `origin: { kind, deviceId }` on every definition. `kind: "client"` requires `transport: "http"`
  and a `deviceId` the server can resolve, and it is what makes `harness/environment.js` tell the
  agent *"runs on Al's PC — its tools act on that machine, not this one."* Without it the agent
  cannot tell your `read_file` from the host's, and it will confuse them the first time both
  exist. **Your job is to make the human set it, not to set it yourself.**

**Securing the listener.** The dashboard's add-server form offers a URL and nothing else — there
is no header field in the UI, even though `normalize()` accepts `headers`. So a bearer token the
user pastes in is not currently available to you. Do this instead:

- Bind **only** to the Tailscale interface address, never `0.0.0.0` and never a LAN address.
- Include a **long random secret as a path segment** in the URL (`/mcp/<32 bytes base64url>`),
  regenerated on demand from the settings page. It rides in the URL field the form does have.
- Reject any request whose remote address is not the configured Doca host's tailnet address.
- All three, not one of them. And **flag the missing headers field to me** — a proper
  `Authorization` header would be better than a secret in a URL, and adding that input to the
  dashboard form is a small server-side change I would rather make than have you work around
  forever.

**Tool design.** Start with a small, honest set, each with `readOnlyHint` set correctly — Doca
turns that into the `danger` flag on the tool switch, so getting it wrong mislabels the UI:

| Tool | Read-only | Notes |
|---|---|---|
| `list_windows` | yes | Title, process, monitor, whether it is minimised |
| `screenshot` | yes | Args: window id or monitor; returns text with the media id (§5.4) |
| `get_clipboard_text` | yes | Text only. Never images or file lists in v1 |
| `set_clipboard_text` | no | The first tool that changes anything; behind its own consent switch |
| `open_url` | no | Opens in the user's browser. Validate the scheme — `http`/`https` only |

Each tool is individually switchable in the DocaDesk settings, on top of Doca's own per-tool
switches. Two independent off switches is correct here: the user of this machine should not have
to trust the dashboard's configuration to keep their desktop private.

Anything beyond this table — running programs, sending keystrokes, reading arbitrary files — is
§10 and needs a conversation first.

### 5.6 Metrics, surfaces and profiles

The dashboard already draws all of this in the WebView, so the native side is deliberately thin: a
tray tooltip and a compact always-available panel driven by the device profile, so the user can
see the host's state without opening a window.

Read the profile with `If-None-Match`, apply `metrics[]` and `maxItems` **client-side** as the
protocol requires, and render by metric *kind* — never by a hard-coded list of metric ids.

**Settings change this device. Profiles change what a device shows.** Say it in the UI copy and
keep the two in visibly different places. This distinction caused more confusion than anything
else across both existing clients; you get to not repeat that.

### 5.7 Screen streaming — last, and only if asked

Deferred on purpose. `/api/v1` has no video transport: media uploads are one-shot, size-capped
blobs, and there is no WebRTC, no long-lived binary channel and no server-side relay. Streaming
means either a new server-side transport or a direct client-to-client path, and both are real
design work rather than a feature to bolt on.

**Do not start this without talking to me.** When we do, the honest starting point is probably a
low-rate sequence of `screenshot` results rather than a video codec, and the first question is
what the agent actually needs — a model reading a screen every few seconds is not the same problem
as a human watching a desktop.

### 5.8 Settings

App-local, in one place, with the consent switches most prominent:

- Server URL and pairing state, with an unpair that actually revokes.
- The MCP listener: on/off, URL with copy, regenerate secret, per-tool switches, an audit log of
  what was called and when.
- Capture consent, per-monitor and per-window-allow-list if you can, global if you cannot.
- Notifications: which event types raise one.
- Start with Windows, start minimised.
- The profile is *not* here — it is server state (§5.6).

---

## 6. Cross-cutting behaviour

### 6.1 The audit log

One append-only log the user can read, in the UI, of every tool call and every capture: what,
when, by which MCP session. Rotate it, cap it, never redact it to the point of uselessness. This
is what makes §2 rule 3 verifiable rather than a promise.

### 6.2 Offline and degraded

Four distinct states, and conflating them is the usual bug:

1. **No server reachable.** Show it plainly, with the URL being tried and a retry. The dashboard
   WebView shows *your* page, not Chromium's error page. Cached metrics may be shown, clearly
   marked as stale with their age.
2. **Reachable but unpaired.** Offer pairing. Do not show a broken dashboard.
3. **Paired but revoked.** A 401 with a revoked token is terminal: stop the push loop, stop the
   MCP listener (its tools are worthless if the harness is not going to call them and the device
   is no longer trusted), and say what happened.
4. **Server up, harness down.** Perfectly normal — `/api/harness/status` can report
   `ready: false` with no provider configured. Not an error state for this app.

The MCP listener's availability is independent of all of this: the harness reaching you does not
require you to be reaching the harness. Do not gate the listener on the push loop being healthy.

### 6.3 Errors

Map the protocol's error codes once, in `DocaDesk.Core`, to a small set of app-level outcomes, and
test the table (§8). `PROTOCOL.md` has the full list. Anything unmapped degrades to "unexpected
server error" with the code logged — never swallowed.

---

## 7. Security requirements

Its own section so it cannot be skimmed past.

**7.1 Credential storage.** The device token goes in the Windows Credential Manager (or DPAPI with
`CurrentUser` scope). Never in `appsettings.json`, never in the registry in plaintext, never in a
file beside the exe. The MCP path secret is a credential too, and gets the same treatment.

**7.2 Transport trust.** The tailnet host has a real certificate, so the default path is normal
validation with no custom callback. A self-signed server is supported only via an **explicit,
user-visible pin** captured at pairing: pinned and matching is accepted, pinned and mismatched
**fails hard with no CA fallback**, unpinned self-signed is refused. Never a
`ServerCertificateCustomValidationCallback` that returns `true`. DocaWear's certificate matrix
(§6 there) is the set of cases to test.

**7.3 The listener.** §5.5: tailnet interface only, secret path segment, remote-address check. The
listener is off by default on a fresh install and its state is visible in the tray.

**7.4 Capture.** Do not suppress the capture border where the OS offers one. A visible indicator
that the screen is being read is a feature for the person sitting in front of it, even when it is
mildly ugly for the person reading it. If a specific case genuinely needs it off, that is an
ask-first item with a reason.

**7.5 Logging.** Redact tokens, the MCP path secret, and anything from the clipboard. A clipboard
tool that logs its own return value has just written the user's password to disk. Screenshot logs
record *that* a capture happened and of what window — never the image bytes.

**7.6 No unattended input.** No keystroke or mouse injection in v1. It is in §10 and it is the
single change most likely to turn this app into something I would not install.

---

## 8. Testing requirements

xUnit in `tests\DocaDesk.Tests`. Everything here must run with **no server and no display**,
except where it says otherwise.

- `DocaDesk.Core` against a stub HTTP server (a plain `HttpListener` is enough; `WireMock.Net` is
  fine if you prefer and counts as already-discussed): model leniency with unknown fields and
  unknown enum variants, the full error-mapping table, cursor persistence across process restart,
  `resync` handling, gap-tolerant `seq`, `selectionId` idempotency and the new-id-after-`back`
  rule, credential round-trip, log redaction.
- A **heartbeat watchdog test**: no `: ping` for 50 s triggers a reconnect. Use an injectable
  clock; do not write a test that takes fifty seconds.
- The certificate matrix from §7.2, all five cases.
- **The MCP listener tested against Doca's own client.** This is the highest-value test in the
  suite and it is easy: `node -e` a script that requires
  `D:\doca\doca\DOCA\modules\mcp\client.js`, points an `McpClient` at your listener with
  `transport: 'http'`, and asserts `initialize` and `tools/list` succeed. If that passes, the
  dashboard will work. If you unit-test only against your own expectations, you will discover the
  handshake mismatch by hand in the UI.
- Listener authorisation: a request without the secret path, and a request from a non-tailnet
  address, are both refused — as tests, not as a manual check.
- Capture: the `PrintWindow` fallback selection logic, unit-tested with the platform calls behind
  an interface. Actual capture needs a display and is a manual acceptance step.
- End to end against the live server (§1.1) with `agent-sim.js` raising real prompts, before any
  milestone is called done.

---

## 9. Milestones

Report back after each (§11). Do not start the next before I have seen the previous.

**M1 — Foundation.** Solution, the four projects and the test project. Nullable on, warnings as
errors in `Core`. Hand-written models for the protocol surfaces you need, `DocaClient`, credential
store, error mapping, redacting logger. Tests per §8 for `Core`. **Report the exact SDK, Windows
App SDK and WebView2 versions you used**, and anything in §3.2 that turned out to be wrong.

**M2 — Pair and show.** Pairing from the dashboard with nothing typed, the certificate model, the
capabilities from §3.4 read off the real machine, and the dashboard in WebView2 with tray and
single instance. Acceptance: pair against the live server, see the real dashboard, close the
window and have the app still be running in the tray; revoke the device from the dashboard and
watch the app say so honestly.

**M3 — Push loop.** SSE with the 50 s watchdog, backoff from `capabilities.push`, cursor
persistence, `hello`/`resync`/`close`, acks, the JSON-poll fallback on the same cursor.
Acceptance: kill the app mid-stream and lose nothing; force a `resync` and recover.

**M4 — Prompts and notifications.** §5.3 in full. Acceptance: `agent-sim.js` raises a prompt; it
is answered from the notification with the dashboard window closed; the same prompt answered on
the phone first closes cleanly here; a text choice is typed and submitted with Enter.

**M5 — Screenshots.** §5.4 including the media upload path, the size budget, the audit line and
the tray indicator. Acceptance: a screenshot taken from a script, visible in the dashboard's media
by id, with an audit entry and a visible indicator at the time it was taken.

**M6 — The MCP server.** §5.5: listener, auth, the five tools, per-tool switches, the settings
page with the copyable URL and the registration instructions. Acceptance: **register it in the
real dashboard with `Runs on: a paired client`, start it, and have the harness call `list_windows`
and `screenshot` on this machine in a real conversation.** Then confirm the agent's environment
block names this device — that is the whole point of `origin`.

**M7 — Metrics panel and settings polish.** §5.6 and §5.8.

Streaming (§5.7) is not a milestone. It is a conversation.

---

## 10. Explicit non-goals

- **Do not** reimplement any dashboard screen natively — no file manager, terminal, logs, docker,
  config editor or model manager. They are in the WebView.
- **Do not** write to Doca's own configuration: no `POST /api/mcp`, no `/api/vms`, no prefs, no
  `openclaw.json`. §2 rule 6.
- **Do not** create, start or control virtual machines. `modules/vms.js` is local-only by design
  and client-hosted VMs are deliberately deferred — it is recorded in the server's `TODO.md`.
- **Do not** implement keystroke or mouse injection, or any general "run this program" tool.
- **Do not** implement artifacts execution or any `exec` capability. `exec: []` stays.
- **Do not** add a JavaScript bridge between the WebView and native code (§5.2).
- **Do not** embed a model, hold a provider API key, or run an agent loop.
- **Do not** build screen streaming (§5.7).
- **Do not** implement quiet hours, choice filtering or prompt targeting client-side.
- **Do not** hold the `agent` or `*` scope.
- **Do not** hard-code any surface, metric, command, limit, threshold or colour.
- **Do not** hide the OS capture indicator (§7.4).

---

## Appendix A — Constants, for reference only

**Read these from `GET /capabilities` at runtime.** They are recorded here so you can sanity-check
behaviour, not so you can hard-code them (§2 rule 1).

| Group | Values |
|---|---|
| Payloads | `snapshotBytes` 16384 · `promptBytes` 32768 · `eventBytes` 32768 · `imageBytes` 204800 · `mediaBytes` 1572864 · `audioBytes` 1048576 · `audioSec` 30 · `artifactBytes` 262144 · `extBytes` 8192 · `varsBytes` 16384 |
| Push | `heartbeatSec` 25 (reconnect at 50) · `retainedEvents` 500 · `retainedHours` 24 · backoff 1000 ms → 60000 ms, factor 2, jitter 0.2 |
| Prompts | ≤8 choices · label 40 chars · title 120 · TTL 30 s–7 d, default 3600 s · pending timeout 90 s · ≤24 blocks · voice default 20 s (max 30) · text default 280 chars (max 2000) |
| Sensors | `sensorMaxRateHz` 50 · `sensorMaxDurationSec` 600 · `sensorBatchMax` 500 |
| Profile | `refreshSec` 2–3600, default 10 · ≤16 pages · ≤12 surfaces per page · ≤32 metrics per surface · `maxItems` 1–40, default 10 |
| Rendering | figure/chart capped at 480 px · ≤4 metrics per chart · sparkline ≤60 points |
| MCP | tool names reach the model as `mcp__<server>__<tool>`, max 64 chars, `[A-Za-z0-9_-]` only · `tools/call` timeout 120 s on the Doca side |
| Misc | pair code TTL 300 s · token rotate grace 60 s · `minRefreshSec` 2 · lists truncated at 40 |

---

## 11. How to report back

After each milestone:

1. What you built, and anything in this brief you had to deviate from — with the reason.
2. Anything where `PROTOCOL.md` and this file disagreed, and which you followed.
3. The exact dependency and SDK versions, especially where §3.2 was out of date.
4. The tests you added and their results.
5. A screenshot of the app.
6. **Anything in this brief that turned out to be wrong, impossible, or ambiguous — with what you
   did instead and why.** This is the most valuable part of the report. It is the only channel
   that improves the next brief, and both existing client briefs have been amended because of it.
7. Anything you think is a **server-side** gap rather than a client problem. The missing
   `headers` input on the dashboard's MCP form (§5.5) is one already; there will be more, and the
   server repo is right here.

**Ask me before:** adding a dependency not already named in §3.2, changing the project
boundaries, declaring a new capability, adding a JavaScript bridge, adding an MCP tool beyond the
§5.5 table, suppressing the capture indicator, starting on streaming, or writing anything to the
Doca server's own configuration.
