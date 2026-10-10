# AGENTS.md

## What this is

DocaDesk is the **Windows desktop client** for a DOCA server (`..\doca`, the "OpenClaw
Dashboard" — a Node/Express app exposing a device-agnostic client API at `/api/v1`). It does three
things: it shows the real dashboard in WebView2 with a tray icon, native notifications and a
prompt window; it **hosts its own MCP server** over HTTP on the tailnet so DOCA's harness — which
runs on a Linux host — has tools that act on *this* machine; and it **forwards MCP servers running
on this Windows machine** through that same listener. The third is the newest part and the least
documented anywhere else.

DOCA is the authority for the protocol. `d:\doca\doca\PROTOCOL.md` is normative,
`modules/mcp/client.js` is the exact code that calls this app's listener, and
`.agent/DOCA_DESK_BRIEF.md` plus `.agent/DOCA_DESK_ADDENDUM_MCP.md` are the briefs this repo was
built from — the addendum supersedes the brief **on MCP registration only**.

**Two sibling clients exist and this repo never mentions them.** `..\DocaMobile` (Android phone,
Kotlin) and `..\DocaWear` (Wear OS watch, Kotlin) are the other first-party clients of the same
server, each with its own `AGENTS.md`. They matter here for one reason: DocaDesk is the **only**
client that hosts an MCP server, so anything about hosting, consent or tool origin is unique to
this repo and has no counterpart to copy from — while anything about pairing, caps, scopes or the
push loop has two other implementations to check against before inventing a third answer.

| Project | Contains |
|---|---|
| `src\DocaDesk` | The WinUI 3 app: window, tray, WebView2 host, prompt window, notifications, consent UI, settings, and `Services\McpHost.cs` which owns the listener's lifecycle |
| `src\DocaDesk.Core` | Protocol client: models, `DocaClient`, DPAPI credential store, push loop and cursor, error mapping, `RedactingLogger`. No UI dependencies. **`TreatWarningsAsErrors` is set for this project alone** (`Directory.Build.props:6`) |
| `src\DocaDesk.Mcp` | The MCP listener, its hand-rolled HTTP server, the stdio client for local servers, the local-server registry, and the **tool families** this device offers the harness (`ToolFamilies`, `FamilyConsent`, `FamilyTool`, `DeviceHands`, and one file of tools per family group). Must not reference WinUI — the listener has to be startable from a test with no window, and the Linux client reuses the families unchanged |
| `src\DocaDesk.Capture` | `Windows.Graphics.Capture` with a `PrintWindow`/GDI fallback, encoding and downscaling |
| `tests\DocaDesk.Tests` | xUnit, 163 tests (2026-10-07) |

**`DocaDesk.sln` now contains all five projects** (`dotnet sln list`, verified on portal
2026-09-27). It used to hold only `src\DocaDesk.Capture` and `src\DocaDesk`, and both this file and
`TODO.md` went on saying so long after that changed — which is why the paragraph below about
`dotnet test` was wrong too. **This claim and the test count are the two sentences here most likely
to rot: run `dotnet sln list` and read the suite's own total rather than trusting either.**

## Start here: `ISSUES.md` and `TODO.md`

**Read both before you touch anything.** They are the handover. Three of the four open issues are
the *second* attempt at the same symptom, which is what happens when a session starts without them.

- **`ISSUES.md` — live defects.** Behaviour nobody chose, seen on a real Windows machine. Ids are
  `D-n` and never change; cite the id in the commit subject.
- **`TODO.md` — deliberate omissions.** A decision with its reason. When a deferred item turns out
  to bite someone it gets promoted to `ISSUES.md`; when an issue turns out to have been a decision
  it moves back with the reason. Do not silently drop either.

Five rules:

1. **Write the entry before the patch.** Naming the file and symbol is most of the work.
2. **A fix is not a close.** Move to `## Fixed` only after it has been *run* on portal and the
   symptom is gone. The cloud container can build this repo; it cannot run WinUI, so **every close
   here is a Windows observation**, made by a person or reported back from that machine.
3. **A partial fix stays open, with a note on what landed.** `D-1` is the standing example: the
   close *policy* shipped, the *hang* did not. Marking it fixed because the visible half is done is
   how it comes back a third time.
4. **Never delete an entry.** Fixed ones move down with the version that fixed them.
5. **Every release gets a row in the review log.** A release with no row is unreviewed — which is
   what 2.23.0 is.

**One invariant that is not negotiable and is easy to break by accident:** destructive operations
need a human at this keyboard. Nothing DOCA sends, and nothing an agent decides, may remove a local
MCP server definition. Today that holds because the host-facing surface is `PATCH /mcp/self`
(`url` and `headers` only) and the only caller of `LocalMcpRegistry.RemoveAsync` is one dialog in
`MainWindow.xaml.cs`. Both halves of that are load-bearing — see `ISSUES.md` → `D-3`.

## Running / building

- `dotnet build DocaDesk.sln` builds all five projects.
- **`dotnet test` with no argument now runs the suite** — the test project is a solution member, so
  bare `dotnet test` reports `Passed: 163` (2026-10-07). This file used to say it *"builds nothing, runs nothing,
  and exits 0"*, which was true once and is the kind of stale warning that makes a reader distrust a
  green run. Naming the project — `dotnet test tests\DocaDesk.Tests` — is still the habit worth
  keeping: it is faster and unambiguous.
- SDK is pinned to `9.0.318` with `rollForward: latestFeature` (`global.json`).
- The app project targets **`net9.0-windows10.0.19041.0`** with `TargetPlatformMinVersion`
  `10.0.17763.0`, `Platforms x64;ARM64`, defaulting to `x64` / `win-x64`
  (`src\DocaDesk\DocaDesk.csproj:4,5,8,17,18`). It is unpackaged (`WindowsPackageType=None`) with
  the Windows App SDK self-contained. **The test project also targets `net9.0-windows…`**
  (`tests\DocaDesk.Tests\DocaDesk.Tests.csproj:4`), so the suite needs Windows — there is no
  Linux build agent story here, and there is no point writing one.
- **You cannot relink the app while `DocaDesk.exe` is running.** The build fails with `MSB3027`
  and `MSB3021` after ten retries on `DocaDesk.Core.dll`, `DocaDesk.Capture.dll` and
  `DocaDesk.Mcp.dll`, naming the process that holds them. To check that a change compiles without
  closing a running instance, build into a scratch tree:
  `dotnet build src\DocaDesk\DocaDesk.csproj -p:BaseOutputPath=$env:TEMP\ddscratch\`. That
  succeeds while the app holds its own `bin`.
- **`dotnet test tests\DocaDesk.Tests` is not affected by the lock.** Core, Mcp and Capture build
  into their own `bin` directories, which the running app does not hold; only the app project's
  output folder is locked.
- There is **no build step beyond MSBuild and no lint script**. No analyzer package, no
  `.editorconfig` rules enforced in CI, no formatter. `TreatWarningsAsErrors` on `DocaDesk.Core`
  is the only thing that turns sloppiness into a failure.

## The MCP listener (`src\DocaDesk.Mcp\McpHttpListener.cs`, `McpDispatcher.cs`)

Hand-rolled JSON-RPC 2.0 over HTTP: POST in, JSON out, on `SimpleHttpServer` — a raw `TcpListener`
chosen to avoid `HttpListener`'s URL ACL requirement. The server itself is `McpDispatcher` (one per
start, shared by both transports — the listener here and the socket below); it answers `initialize`, `tools/list`,
`tools/call` and `ping`, reporting `protocolVersion "2025-06-18"` and `serverInfo.name "DocaDesk"`
(`:235-239`). **That shape is dictated by `modules/mcp/client.js`, not by a spec.** Read that file
before changing anything here. Since DOCA 2.90.0 it holds the GET event stream open when
`initialize` says `tools.listChanged`, which this listener now does: `NotifyToolsChanged()`
(wired to `LocalMcpRegistry.ToolsChanged` in `McpHost.cs`) tells DOCA to list the tools again, so
a local server started later reaches the dashboard without ↺ Tools (`ISSUES.md` → `D-8`).

The security invariants are **all of them, not one of them**:

- **Secret path.** The URL is `http://<bind>:<port>/mcp/<32 random bytes, base64url>`
  (`:100-104`, `:144`). A path that does not match byte for byte gets 404 (`:177-178`).
- **`IsRemoteAllowed` runs before the path is even compared** (`:172-173`, `:198-216`). A loopback
  peer is allowed **only** when `AllowLoopback`; otherwise the peer must resolve to
  `AllowedRemoteHost` — set to the DOCA server's host name in `McpHost.cs:98,103` — or its address
  must start with `100.`.
- **No wildcard bind, ever** (`:136-137`). `0.0.0.0` and `::` throw rather than being normalised.
  With no explicit `BindAddress` the listener uses `FindTailscaleIpv4()` (`:107-122`) and
  **refuses to start when there is none** (`:129-134`); the message is the user-facing sentence,
  which is why it is set on `LastError` before the throw.
- **A wrong bearer answers exactly like a wrong path: 404 with the body `not found`**
  (`:180-189`). Do not "improve" this into a 401 — the entire point is to not confirm to a prober
  that the path was right.
- **`EnforceBearer` starts false** (`:66`) and is flipped on only once the host record is seen to
  carry an `Authorization` key (`McpHost.cs:225-231`). The order matters and it is the safe one:
  offer or patch the header, *then* enforce. Enforcing first locks the host out of a listener it
  had working, and there is no channel through which it could tell you.
- **`StartLoopbackForTestsAsync` is test-only** (`:148-159`) and sets `AllowLoopback = true` as a
  side effect. Never call it from app code; note the side effect if you are reading a test that
  seems to prove loopback is refused.
- A call is bounded by `ToolCallTimeout`, default 120 s (`:77`), matching `client.js`'s own 120 s
  on `tools/call`. A timeout returns an `isError` content block (`:304-308`), not a dead socket —
  the model has to be able to read what went wrong.

## The socket transport (`src\DocaDesk.Mcp\McpSocketHost.cs`, PROTOCOL.md §22.2)

Settings → This device → **Connect out to the hub** (`AppPrefs.McpOverSocket`, off by default) replaces the listener
with a socket this PC opens: `wss://<hub>/api/v1/mcp/host` with the device token as a bearer, the certificate trusted as
`DocaClient` trusts it (`TrustsCertificate`: the pin, else the CAs). The hub sends JSON-RPC requests, one per text frame;
each is answered by its id through the same `McpDispatcher`, concurrently (a long `tools/call` does not hold up the
next). A notification (no id) is never answered. A keepalive notification every 25 s; `NotifyToolsChanged()` sends
`notifications/tools/list_changed` down it. Dropped → dialled again, 1 s doubling to 60 s. **Close 4000 means a newer
connection from this device replaced this one — not fought over**, the loop stops and says so (DocaDesk open twice).

- The offer is `{transport: "socket", label, tools, note}` with no URL. `ReconcileRegistrationAsync` treats a definition
  of the other transport like a 404: it offers afresh, and accepting replaces the old one on the hub (same label, same
  slug). A socket definition is never PATCHed — there is no address to correct.
- No Tailscale address is needed, and none of the listener's invariants (secret path, bearer, remote check) apply:
  the socket is outbound, authenticated by the device token, and only the hub is at the other end.

## Sealed secrets (`SealedSecrets.cs`, `WindowsSecretSink.cs`, `DashboardHost.FillAsync`; PROTOCOL.md §22.3)

The hub hands this PC a secret for one use, sealed for this device alone; the agent never sees it. Exactly what
`docs/api/sealed-secrets.md` (hub) asks of DocaDesk, with its tightened rules:

- **The key**: `GET /api/v1/mcp/self/seal`, taken on every connect (`McpHost.TakeSealKeyAsync`), DPAPI as
  `mcp.seal.key` (key and `aad` as JSON). The device id is the `aad` after `doca-seal:` — DocaDesk stores no device id
  of its own. A 404 hub has no sealed secrets.
- **`secret_fill` is answered by the dispatcher and never listed.** AES-256-GCM (`iv` 12 bytes, `data` = ciphertext ‖
  16-byte tag); refused: another device, `|now − iat| ≥ 5 min`, a nonce seen in the last 10 min. The answer is
  `{done, uses, counted, seconds}`; **the value is never in an answer, an audit line or an error** — an OS failure is
  reported by exception type only.
- **A payload with an `origin` is only ever a `field`** — `type`/`clipboard` refuse it. `field` fills a credential input
  (password, or autocomplete `current-password`/`new-password`/`one-time-code`) of the page the panel's WebView2 shows,
  only on exactly that origin (checked in C# against `CoreWebView2.Source`, and again in the page). The value is a
  JSON-encoded argument to a fixed script, never spliced into its text. **DocaDesk has no page snapshot, so `ref` counts
  the page's credential fields in document order from 1; 0 = the focused one, else the only one.** `tab` must be absent.
- `type`: `SendInput` `KEYEVENTF_UNICODE` (the `input_type` path). `clipboard`: Win32 with
  `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`, `CanUploadToCloudClipboard = 0`;
  cleared after `ttlSec` only if it still holds the secret (SHA-256 compare); `counted: false`.
- **The read hold**: for 60 s after any use, and while a secret is on the clipboard, `SealedSecrets.Reads` (`shell`,
  `shell_job`, `elevated_run`, `processes_start`, `files_read`, `screen_capture`, `screenshot`, `get_clipboard_text`) are
  refused by the dispatcher with a sentence. Add a new tool that reads back to that set.
- It needs the **`input` family** usable (granted, not revoked) — the family the person lent for typing.

## A dialog waiting in the hidden panel (`Core\PanelAttention.cs`, `PanelAttentionHost.cs`, `DashboardHost.OnWebMessage`)

DocaDesk mostly sits in the tray, and the panel inside it opens dialogs only a person can answer: the step-up
password after 12 hours, a guarded switch's password, a question or confirm, the agent's approval popup. When one opens
while the window is not in front showing the panel, DocaDesk shows a Windows notification (tag `panel-attention`, fixed
words per kind, "Notify me of prompts" governs it; clicking opens the window) and marks the tray icon until it is
answered. Answered, or the window brought forward: the notification is taken back. Each kind is told once per opening.

- **The one script and the one bridge in the panel window** (M2 said none). `PanelAttention.Script`, added with
  `AddScriptToExecuteOnDocumentCreatedAsync`, runs in the top frame only and posts
  `{type: "doca.attention", kinds: ["password"|"question"|"approval", …]}` — the set of open dialogs — whenever it
  changes, and once at load. `OnWebMessage` drops any message whose source is not the hub's own origin, and
  `PanelAttention.Parse` anything not exactly that shape. Nothing goes from DocaDesk into the page this way.
- **No dialog text, field value or command ever leaves the page**: the watcher reads element ids, a class and the
  input's `type`, and a test holds the script to never reading `.value`, `textContent`, `innerText` or HTML. The words
  are `PanelAttention.Words`. The log line says the kinds, whether the window was in front, and what was done.
- **It reads the panel's own ids today** (`app-prompt-modal` + `app-prompt-input`, `app-confirm-modal`,
  `approval-overlay`; DOCA `public/js/lib/dialogs.js`, `agent-ui/approval.js`). If the hub renames them, the watcher goes
  quiet — nothing breaks, nothing is told. The sturdier contract is the hub posting the same message itself; until it
  does, a hub-side rename must change the script here.
- **In front** is visible, the foreground window, not minimised, and showing the panel (not DocaDesk's settings). A
  window merely visible behind another app is not in front, and is told. Windows itself holds back the banner while an
  app is full screen or Do not disturb is on; the notification is then in the notification centre, and the log says it
  was sent.
- Checked on portal 2026-10-07 against hub 2.302.1 (the branch's exe from its own output, the panel driven through a
  loopback DevTools port given to that test run only): a password prompt and an approval in the hidden window each
  raised their notification (read back from the notification history: fixed words only) and the mark; answering
  withdrew it. The in-front case is covered by the tests only — Windows would not hand a window started from a
  scheduled task the foreground.

## Tool consent (`ToolConsent`, `McpHttpListener.cs`)

- **Everything is off.** The dictionary is seeded with the five desk tools all `false` (`:28-35`),
  and `McpHost.InitializeAsync` restores each from HKCU with `false` as the fallback
  (`McpHost.cs:79-80`, `AppPrefs.cs:35`).
- **`Set` deliberately ignores a name it does not know** (`:38`). A name has to be registered
  before it can be flipped, so neither a typo nor a stale UI can silently grant something.
- `Register` / `Remove` (`:47-53`) exist for tools that appear at runtime — a local MCP server's
  tools do not exist when this class is constructed. `LocalMcpRegistry` registers them with their
  server's consent flag on start (`:265`) and removes them on stop (`:203`).
- **The listener checks consent on every `tools/call`** (`:287-295`), never once at `tools/list`
  time. The host caches nothing for long, but a tool can be revoked between a list and a call, and
  the tool set is dynamic by design. The refusal comes back as a normal MCP result with
  `isError: true` plus an `mcp.denied` audit line — not a JSON-RPC error, because the model should
  read it and move on.

## Local MCP servers (`src\DocaDesk.Mcp\McpStdioClient.cs` + `LocalMcpRegistry.cs`)

`McpStdioClient` **deliberately mirrors DOCA's `modules/mcp/client.js`**, and the mirroring is the
feature: a server has to behave identically whether DOCA spawns it on the host or DocaDesk spawns
it here, or a divergence shows up as "it works on the host" and nobody can tell why. What is
matched, line for line:

| Behaviour | Here | There |
|---|---|---|
| Protocol version `2025-06-18` | `:31` | `client.js:17` |
| `initialize` (20 s) → `notifications/initialized` → `tools/list` (20 s) | `:89-102` | `client.js:185-193` |
| 120 s default on `tools/call` | `:54` | `client.js:225` |
| 200-line stderr ring buffer | `LogLines`, `:32` | `LOG_LINES`, `client.js:18` |
| Absent `readOnlyHint` means *not* read-only | `:134` | `client.js:214` |
| Text blocks joined, other blocks as `[<type>]`, `isError` prefixed `Error: `, empty as `(no output)` | `:154-170` | `client.js:225-231` |
| A server that calls *us* gets `-32601` rather than silence | `:277-293` | `client.js:100-107` |

Keep it that way. The remaining rules:

- **A server that fails to start returns `false`; it does not throw** (`LocalMcpRegistry.cs:164-195`).
  The reason lands in `LastError` and the stderr in `LogOf(id)`, because the panel wants to draw a
  failed row with a **Copy log** button next to it. An unknown *id* still throws
  `KeyNotFoundException` (`:169`) — that is a caller bug, not a server state.
- **Only a `Running` server contributes tools, and only a consented one** (`Tools()`, `:231-243`).
  This is why `McpListenerOptions.DynamicTools` is a `Func<IReadOnlyList<IMcpTool>>`
  (`McpHttpListener.cs:75`) wired to `_localServers.Tools` (`McpHost.cs:108`) and **asked on every
  list and every call** rather than being a list captured at start. A stopped server has to stop
  appearing inside the same process, without restarting the listener.
- Proxied tools are named **`<serverId>__<tool>`**, with anything outside `[A-Za-z0-9_-]` mapped to
  `_`, truncated to `MaxProxiedNameLength = 40` (`:48`, `:245-250`). The budget: DOCA re-prefixes
  to `mcp__<server>__<tool>` and slices the whole thing to 64 characters
  (`modules/mcp/tools.js:14-16,43`), so 40 leaves 19 for `mcp__<DOCA's id for us>__`. It is a
  budget, not a guarantee — if it still does not fit, DOCA truncates and de-duplicates with a `_2`
  suffix (`tools.js:45-51`), which is survivable but makes a tool name unrecognisable.
- **Consent is per server, not per tool** (`SetConsent`, `:132-145`). One flag registers every tool
  of that server in `ToolConsent` at once.
- **There is deliberately no environment block on a server definition.**
  `LocalMcpServerSpec` is `Id`, `Label`, `Command`, `Args`, `WorkingDirectory`, `AutoStart`,
  `Consented` (`:17-24`) — no `Env` — even though `client.js` accepts one (`client.js:57`).
  `mcp-servers.json` is plain JSON in `%LOCALAPPDATA%`, and an environment block is precisely
  where an API key would land. Do not add one without first deciding where the secret lives; DPAPI
  is already in `DocaDesk.Core.Security`.
- **Only the person at the keyboard may add a definition**, because a definition is a command line
  that gets spawned. Nothing arriving over the wire can create or edit one: there is no `/api/v1`
  route that would (`PATCH /mcp/self` takes `url` and `headers` only), and this app never calls
  DOCA's legacy unauthenticated `POST /api/mcp`. `Add` validates the id against
  `^[a-z0-9][a-z0-9-]{0,31}$` and requires a command *before* anything is spawned (`:94-118`).
  This is the same rule DOCA applies to its own agent (`mcpServers` absent from `SETTABLE`) and to
  every device (`mcp:define` does not exist).
- The store is written tmp → `.bak` → move (`:306-316`), and a corrupt file is recorded in the
  audit log and otherwise ignored (`:282-303`). A first run with no file at all is the normal case.

## Devices as hands (`src\DocaDesk.Mcp\` — `ToolFamilies`, `FamilyConsent`, `DeviceHands`, `FilesTools`)

`PROTOCOL.md` §22.1 and `docs/design/devices-as-hands.md`: a paired device offers the harness the
same **tool families** the host has, as far as its OS allows. All four files are **WinUI-free and in
`DocaDesk.Mcp` on purpose** — the Linux client is meant to reuse them unchanged (design §6 step 3).
Only the Settings page and the HKCU storage live in `src\DocaDesk`.

- **The nine family names are DOCA's** (`modules/devices-control.js:25`), and a grants report keeps
  only the keys whose value is a boolean (`:89`). So an unknown name is dropped **in silence** — a
  typo is not an error, it is a family that never gets offered and no message saying why. `FamilyConsent.ReportBody()`
  therefore sends **all nine explicitly**, `false` included; omitting one leaves whatever DOCA held.
- **Granted and revoked are two facts, not one flag.** *Granted* is the person's answer, asked once
  per family in our UI and remembered in HKCU as `Family.<name>` — where **absent ≠ false**, which
  is why `AppPrefs.GetFamilyGrant` returns `bool?` and does not use `ReadBool`'s fallback: "never
  asked" is what drives the prompt. *Revoked* is DOCA taking a family back (`device.control`
  `revoke`/`restore`). A `restore` returns the family to whatever the person had said — it can never
  widen access they did not give. Same rule as `mcp.listener` `stop`: a host request is not
  permission to overwrite an explicit local choice.
- **Every `device.control` action is acked, including the ones that fail.** DOCA keeps a 20-entry
  history with `ackAt`/`ok`/`detail` and that is what a person reads in Settings → Devices. Note
  that **DOCA does half the work itself** — it closes the stream for `reconnect`/`disconnect` and
  ends the device's sessions for `disconnect` (`devices-control.js:63-65`) — so an action *looks*
  successful against a client that handles nothing. **The ack is the only evidence this side ran
  it.** The ack is deliberately not sent under the action's own cancellation token, because
  `disconnect` is precisely the action that tears that token down.
- **`FilesTools` answers `files_list/read/write/mkdir/move/copy/delete`** for DOCA's Files tab
  (`modules/device-files.js`). Three shapes are dictated by that file and each fails *quietly* if
  guessed: results are **JSON text** (`JSON.parse`, else 502); a refusal is **`isError: true`**
  (`client.js` prefixes `Error: `, which `device-files.js:44` turns into a 400 — a refusal returned
  as plain JSON would parse as *success*); and the names are **bare**, never `<server>__<tool>`.
  Serialisation deliberately does **not** use `DocaJson.Options`, whose `WhenWritingNull` would drop
  the `size: null` on a folder that the host's own shape includes.
- **There is no path jail, and that is a decision.** Design §1 gives DocaDesk files *"everywhere the
  user can"*; the gates are the one-time grant plus DOCA's revoke. A root would also break the Files
  tab's machine selector.
- **The family tools gate on `FamilyConsent`, not `ToolConsent`.** They are registered in
  `ToolConsent` as always-on and refuse internally, because a family is one question asked once —
  not seven switches. Do not "fix" this into per-tool toggles without reading design §2.
- **Implemented on Windows: `files`, `shell`, `processes`, `screen`, `input`, `apps`, `elevated`**
  (`ToolFamilies.Implemented`, per OS; Linux: `files`, `shell`, `processes`, `apps`). `device` and
  `mcp` are reported `false` everywhere and drawn disabled — see `TODO.md`.
- **A family's tools are listed only while it is usable** (`FamilyTool.Offered`, composed into
  `DynamicTools` in `McpHost.StartAsync`). DOCA's `modules/mcp/tools.js` exposes every tool a
  device lists, whatever its grants, so this listener is what makes "a tool the device would refuse
  is not shown" true (`ISSUES.md` → `D-17`). `FamilyTool` refuses a call anyway, for the race
  between a list and a call. A grant or revoke calls `NotifyToolsChanged()`, so DOCA re-lists.
- **Names.** `shell`/`shell_job` are the **host's own names and arguments**, on purpose — one set of
  names on every machine. The rest (`processes_*`, `screen_windows/capture`, `input_*`, `apps_open`,
  `elevated_run`) follow `files_*` and are **not in `PROTOCOL.md` yet**: this client's proposal,
  which the Linux client must copy exactly (`D-18`).
- **Where each lives.** Portable in `DocaDesk.Mcp`: `ShellTools`, `ProcessAppTools`. Windows-only but
  WinUI-free, also in `DocaDesk.Mcp`: `WindowsTools` (`SendInput`; UAC via `runas`). In the app:
  the two `screen` tools, because they need `DocaDesk.Capture` — they **wrap** `list_windows` and
  `screenshot`, so there is one capture path.
- **`elevated_run` goes through UAC every call**, and a declined prompt (`ERROR_CANCELLED`, 1223) is
  an answer the agent reads. The command is written to the audit log *before* the prompt, because
  UAC cannot show it (it travels as `-EncodedCommand`).
- **`FamilyConsent` raises events only on a real change, and `GrantsChanged` is separate from
  `Changed`.** A report writes DOCA's revocations back into it; when that raised the event the report
  listens to, one toggle fanned out without end (`D-19`). `DeviceHands` owns the report-on-change
  wiring so the Linux client inherits it.
- Background shell jobs log to `%TEMP%\docadesk-job_*.log` and are **killed on quit**
  (`ShellTools.StopAllJobs` from `McpHost.DisposeAsync`).

## Host registration (`src\DocaDesk\Services\McpHost.cs`)

- **One reconciliation path**, `ReconcileRegistrationAsync` (`:163-242`):
  `GET /api/v1/mcp/self` → 404 means offer; **200 always means `PATCH /mcp/self { url, headers }`**.
  It used to compare first and skip the write when the URL matched and a header key was present —
  do not put that back. `GET` **masks header values**, so a rotated bearer is indistinguishable from
  a matching one, and skipping the write is exactly how a live listener ends up answering 404 to the
  host that still holds the old token. `RegenerateSecretAsync` does not duplicate any of it — if the
  listener was running it restarts, and `StartAsync` reconciles (`:140-154`).
- **`WaitingForAccept` is a normal state, not an error** (`:13-20`, `:193-198`). The offer is
  recorded with a 202 and then a human clicks accept in the DOCA dashboard; until then the UI says
  "Waiting to be accepted in the DOCA dashboard. Not an error." Do not turn it into an error
  banner, and do not retry it as though it had failed.
- **`ReconcileRegistrationAsync(offerOn404:)` is what separates the two callers.** `true` on start
  and after a regenerated secret. **`false` in the 8-second wait poll** (`:244-268`), so the poll
  only observes: re-offering every eight seconds would leave the person in the dashboard a pile of
  offer cards for one machine. There is no push when an offer is accepted, which is why the poll
  exists at all.
- A `mcp.listener` push event is **a request, not a command** (`:277-332`). `start` is refused when
  `AppPrefs.McpAutoStart` is off (`:302-308`) or when `AnyToolConsented()` is false (`:310-316`),
  and the refusal with its reason goes to `RegistrationMessage` and the audit log — the host gets
  no reply channel and does not need one. `stop` stops the listener but **leaves the master switch
  exactly as the user set it** (`:288-297`): a host request is not permission to overwrite an
  explicit local choice.
- Local servers marked `AutoStart` are started **after** the listener is up (`:125`), on purpose —
  a child process is worth spawning once something can reach its tools. Nothing in the registry
  autostarts from a constructor, so requiring these types in a test cannot spawn anybody's
  processes.
- `RegenerateSecretAsync` mints a **new bearer with the new path** and resets `_enforceBearer` to
  false (`:140-154`), so an old header cannot unlock a new URL.

## The panel's look on the app's own windows (hub `device-look`, 2.342.0)

The owner (2026-10-09): the apps' own settings in the panel's look. The hub resolves the look this device's screen
settings draw — `GET /api/v1/settings/look` (PROTOCOL §14.1): colours by role, a `light` or `dark` ground, the style
(`classic`/`modern`/`points`), its font lists and corners — and sends `settings.changed` with `look: true` when they
change on this device's layer or its person's. DocaMobile 1.4.0 does the same (its `panel-look` work).

- **`DocaDesk.Core\Look\`** is the wire and the mapping, pure and tested (`HubLookTests`, on the hub's own fixtures
  copied into `tests\DocaDesk.Tests\hub-fixtures\` and compared with the sibling checkout's when it has them):
  `PanelLook`/`PanelLookWire` read the body and the event; `DeskLook.From` maps it onto WinUI — the ground decides
  Light or Dark, and each theme resource the app draws with takes a role (page ← `bg`, title bar ← `bg2`, card ←
  `surface`, card stroke ← `border`, text ← `text`/`muted`, accent fills ← `accent` with `onAccent` on them, states ←
  `red`/`green`/`amber`). **WinUI's control keys (`ToggleSwitchFillOn`, `AccentButtonBackground`,
  `SelectorBarItemPillFill`…) are `StaticResource` aliases resolved once**, so overriding `AccentFillColorDefaultBrush`
  does not reach them: every control the windows use is named one by one in `DeskLook.From`. A new kind of control
  in Settings needs its keys there (`generic.xaml` in the Windows App SDK package lists them). A palette without
  ground, text or accent is no look.
- **`Services\HubLook.cs`** reads it when the session becomes Paired and on the event, keeps the last one in
  `%LOCALAPPDATA%\DocaDesk\look.json` with the hub it came from (a cold start draws it before the hub answers; another
  hub's is dropped), keeps it through a failure or while Offline, and drops it on unpair or revoke. A 404 (a hub older
  than 2.342) is DocaDesk's own look. **Settings → General → Appearance → Use the hub's look** (HKCU `UseHubLook`,
  on by default) turns it off.
- **`Services\LookApplier.cs`** applies it on the UI thread: one `ResourceDictionary` merged last into the
  application's resources (brushes, `ControlCornerRadius`, `OverlayCornerRadius`, and `DeskCardCornerRadius`, which the
  `Card` style now reads), then each registered window root (MainWindow, the prompt window, the metrics panel) is
  given the look's theme — **flipped through the other theme first**, which is what makes WinUI look its
  `{ThemeResource}`s up again in a window already drawn (seen on portal: switching the look live redraws the window,
  and taking it off restores Windows' own). Fonts cannot go through a resource — the type ramp's styles name their
  font outright — so the applier walks the tree and sets them on each TextBlock and control, marking what it set so
  the own look restores exactly what was there (Consolas stays the code font; icons are skipped). Rows built in code
  (`RefreshFamilies`, the local MCP rows, a prompt's choices) call `LookApplier.Refresh` after building. The caption
  buttons are coloured from the look in `MainWindow.OnLookApplied`.
- **IBM Plex Sans (Regular, SemiBold) and Plex Mono (Regular) are carried** in `src\DocaDesk\Assets\Fonts\` under the
  SIL Open Font License 1.1 (`IBM-Plex-OFL.txt` beside them, copied to the output), loaded as
  `ms-appx:///Assets/Fonts/<file>#<family>` (works unpackaged); text at SemiBold or heavier takes the SemiBold file.
  A family the app does not carry gives way to the next in the hub's list that Windows has (Modern's Inter → Segoe
  UI), as a browser does.
- **Not restyled:** the tray menu (H.NotifyIcon's default `PopupMenu` mode draws a native Windows menu, which follows
  Windows' own light/dark setting), toast notifications (Windows'), and the dashboard itself (the hub's page, already
  in its look).
- **`DocaDesk.exe --look-preview look.json [--section desk]`** opens Settings drawn in a look read from a file (a
  `/settings/look` body or the bare look) and redraws whenever the file changes — its own single-instance key, no
  session, tray, listener or hub, so it runs beside the DocaDesk a person uses. That is how the screenshots of
  Points dark and light were taken on portal (a scheduled task with `/IT`, `PrintWindow` with
  `PW_RENDERFULLCONTENT`). Its "Use the hub's look" switch is disabled; the other switches are the real prefs.
- **Not yet seen against a live hub**: the fetch on connect and on `settings.changed` is unit-tested only on the wire
  shapes; the owner's DocaDesk runs master, and the live hub was not touched.

## Meetings (hub branch `meet-in-apps`, PROTOCOL §23.4)

The owner (2026-10-10): "Video calling + screen sharing in the app." The room is the hub's meeting page in the panel
window; WebView2 has `getDisplayMedia` with its own picker, so unlike DocaMobile there is no capture bridge — the desk
only answers, and every answer is `DocaDesk.Core.Meetings` (pure, `MeetingsTests` on the hub's own frames in
`hub-fixtures\`: `alert-meeting.json`, `meeting.control.json`).

- **Permissions** (`DashboardHost.OnPermissionRequested`, `OnScreenCaptureStarting`): the camera and the microphone are
  allowed to the hub's own address (scheme, host and port, no user info) and denied to any other page; a screen capture
  starts only from the hub's page; every other permission kind is left to WebView2. Nothing is saved in the profile.
- **Join**: an `alert` carrying `meeting {id, link}` (an older hub: the `/meet/<id>` in its text) is an incoming-call
  toast with **Join**, and the tray has **Join the last call** for ten minutes; both load the hub's own `/meet/<id>` on
  the address in use (`DashboardHost.OpenRoom`), never an address from the notice.
- **The controlled-machine banner** (`Services\MeetingBanner.cs`): the hub's `meeting.control` (`state: active`) shows a
  red window on top, at the top of the screen: "<name> is controlling this computer" with **Stop**; `ended` takes it
  away. Stop (or closing it) holds the input tools for up to ten seconds (`DocaDesk.Mcp.MeetingHold`, checked first in
  `input_move/click/type/keys`) so input already on its way does not land, and posts
  `POST /api/v1/meetings/control/stop`, which ends control of this device and tells the room.
- **Not run on portal's desktop yet**: the GUI could not be started beside the owner's DocaDesk (one instance; it is
  relaunched only from master). Built on portal in `D:\doca\DocaDesk-panel-look` (branch `meet-in-apps`), and
  `dotnet test`: 195 passed, 2 failed — `GraphicsCaptureGrabberTests`, which need an interactive desktop and fail the
  same way over SSH ("The specified service does not exist"). To check after release: a meeting's camera and
  microphone without a prompt, Share screen opening WebView2's picker, the Join toast, the banner and its Stop.
- **No version was bumped**: this repo has none yet ("Nothing in this repo sets a version", below).

## Where things are stored

Everything durable lives under `%LOCALAPPDATA%\DocaDesk\`, outside the repo:

| Path | Written by |
|---|---|
| `credentials\<sha256 of key>.bin` | `DpapiCredentialStore`, DPAPI `CurrentUser` scope (`CredentialStore.cs:23-31,64-68`). Keys are `device.token`, `mcp.path.secret`, `mcp.bearer.token`, `tls.pin.sha256`, `server.url`, `mcp.seal.key` (the hub's seal key, §22.3) |
| `mcp-servers.json` (+ `.bak`) | `LocalMcpRegistry.DefaultStorePath()` (`:72-75`) |
| `mcp-url.txt` | **Nothing — and `App.OnLaunched` now deletes it.** An old version wrote the listener URL here, path secret in clear; the write is gone and the comment in `App.xaml.cs` says why. Removing the write left the file behind on machines that had one, holding a live secret (`ISSUES.md` → `D-12`), so startup deletes it best-effort |
| `docadesk.log` (+ `.1`) | `RedactingLogger`'s default sink, one line per call, moved to `.1` past 2 MB (`AppendRotating`; it had grown to 12.6 MB on portal before it rotated) |
| `audit.jsonl` (+ `.1`) | `AuditLog`, appended per entry, 500 kept in memory, rotated at 2 MB (`AuditLog.cs:23-29,76-85`) |
| `event_cursor.txt` | `FileCursorStore` (`CursorAndWatchdog.cs:15-25`) |
| `tray.ico` | `TrayHost`, generated at runtime rather than checked in |
| `look.json` | `HubLook`: the last look the hub gave, with the hub it came from — colours and fonts, nothing secret |

App-local booleans are HKCU `Software\DocaDesk` REG_DWORDs, not a file: `StartMinimized`,
`NotifyPrompts`, `NotifyAlerts`, `McpAutoStart`, `McpOverSocket`, `UseHubLook`, `Tool.<name>` per desk tool, and `Family.<name>` per
tool family — the last one read as `bool?`, because absent means *never asked* (`AppPrefs.cs`).

**The audit log is not redacted, and that is the design.** `RedactingLogger` covers `Bearer …`,
`doca_…` tokens and `/mcp/<secret>` (`RedactingLogger.cs:17-19,36-48`), but `AuditLog.Add` writes
its summary verbatim — brief §6.1 requires the log to stay useful. The consequence is recorded in
`TODO.md`: `mcp.local.add` writes a whole command line, arguments included.

## Tests (`tests\DocaDesk.Tests`)

`dotnet test tests\DocaDesk.Tests` — 163 tests, no server, no display, no API key, no installed MCP
server.

- **`LocalMcpRegistryTests` writes a real stdio MCP server as a `const string` of JavaScript
  inside the test** (`:368-426`) and spawns it with `node`. That is how the whole local-server path
  — handshake, tool list, a successful call, a tool that reports failure, one that never answers,
  one that exits mid-call, persistence across a simulated app restart — is covered with nothing
  installed. `--fail` makes the stub exit immediately to exercise the failure path.
- **`Docas_own_client_can_call_a_server_this_machine_runs` drives DOCA's real
  `modules/mcp/client.js`**: node `require`s it, points an `McpClient` with `transport: 'http'` at
  the listener, and calls `stub__echo` through it — both hops in one test.
  `McpDocaClientHandshakeTests` does the same for `initialize` / `tools/list`. This is the highest
  value test in the suite: if it passes the dashboard will work, and if you only assert against
  your own expectations you will discover a handshake mismatch by hand in the UI.
  - **Never hardcode the path to it.** `DocaRepo.ClientJs` resolves the sibling checkout
    (`DOCA_REPO`, else walk up and try both `doca\` and the old `doca\DOCA\`). A literal
    `D:\doca\doca\DOCA\…` sat there after DOCA's root moved and quietly disarmed **both** tests for
    weeks, D-8's handshake change included (`ISSUES.md` → `D-11`).
  - **Both scripts end with `c.stop()`, and that is load-bearing.** Since D-8 the listener holds the
    GET event stream open, so DOCA's client keeps a live handle and node does **not** exit by
    itself; `stop()` aborts the stream. `process.exit(0)` instead aborts inside libuv
    (`!(handle->flags & UV_HANDLE_CLOSING)`), which reads as a failure after a successful call.
    The node runners are bounded at 60 s and kill the tree, because the unbounded version hung the
    whole run rather than failing (`ISSUES.md` → `D-14`).
- **Run from an SSH session, two capture tests fail** (`GraphicsCaptureGrabberTests.TryCreate_succeeds_when_WGC_supported`,
  `ScreenCapturer_pipeline_ready_when_device_ok`: `COMException: The specified service does not exist as an installed
  service`): an SSH logon has no desktop, so Windows.Graphics.Capture reports supported and then cannot start. Run the
  suite in the person's session instead (a `.cmd` writing to a log, started by a one-shot `schtasks … /IT`): 148 of 148
  there on portal, 2026-10-07.
- **A test that cannot run returns early, so it is reported as *passed*, not skipped.** Every guard
  is a bare `return` — `node` missing (`LocalMcpRegistryTests.cs`), the sibling DOCA repo absent,
  `Windows.Graphics.Capture` unsupported (`GraphicsCaptureGrabberTests.cs:12-15`), non-Windows DPAPI
  (`CoreBehaviorTests.cs:82`), `DOCADESK_E2E_*` unset (`LiveSmokeTests.cs:20-23`). `Skipped: 0` in
  the summary is therefore true and meaningless. **`Assert.Skip` is not available to fix this:**
  `TODO.md` claimed xUnit has had it since 2.9 and the project is on 2.9.2, but `Assert.SkipUnless`
  and friends do not exist in `xunit.assert` 2.9.2 — `error CS0117` — they are xUnit v3. Making the
  summary honest means migrating, which is why the guards are still bare returns. On a machine
  without node, most of the local-MCP coverage evaporates silently. If you need to know a specific
  test really executed, run it under `--filter` and look at its duration: 2 ms means it returned,
  ~200 ms means node ran.
- The live tests are gated on `DOCADESK_E2E_URL` / `DOCADESK_E2E_TOKEN`
  (`McpToolsIntegrationTests`, `LiveSmokeTests`). Note that the token-file fallback in
  `McpToolsIntegrationTests:22-25` resolves one directory too high and never fires — see `TODO.md`.
- The rest is `DocaDesk.Core` against `HttpListener` stubs: model leniency with unknown fields and
  unknown enum variants, the error-mapping table, cursor persistence across store instances,
  `resync`, gap-tolerant `seq`, `selectionId` idempotency and the new-id-after-`back` rule,
  credential round-trip, log redaction, the five-case certificate matrix, and a heartbeat watchdog
  on a fake clock — no test takes fifty seconds.

## Environment gotchas (not bugs)

- **Nothing in this repo sets a version.** There is no `<Version>` property anywhere, so every
  assembly builds as `1.0.0`, while three separate literals each claim `0.1.0`: the listener's
  `serverInfo.version` (`src\DocaDesk.Mcp\McpHttpListener.cs:238`), `DocaDeskConstants.ClientVersion`
  (`:7`) and `DocaClient.ClientVersion` (`src\DocaDesk.Core\Net\DocaClient.cs:18`). DOCA next door
  bumps `package.json` and tags every release; this repo has never had a release number at all. It
  is one property and three literals — pick the number first, then change all four in one commit,
  because a half-done version is worse than none.
- **You cannot get a shell on the machine this is built on, and that is not a broken setup.** The
  repo lives at `D:\doca\DocaDesk` on the Windows host `portal`. Mounting `D:\` into an agent
  workspace has been broken since a Windows update on 2026-09-08, so the loop that works is: write
  the files through the bridge's file-commit tool, leave a `.cmd` wrapper in `D:\doca\` that
  redirects **all** of its output to a log file, have it double-clicked, then read the log back.
  `build3.cmd` is the current shape and closes the running app first for the reason above; copy it
  rather than starting a new one. A wrapper printing to a console nobody can read has verified
  nothing, and a build whose log you never read is a build you are guessing about.

- **The listener needs a Tailscale IPv4 and refuses to start without one.** With Tailscale down,
  flipping the toggle throws and the UI reads "No Tailscale IPv4 found. Connect Tailscale before
  starting the MCP listener." That is the invariant working, not a failure to handle a case.
  `FindTailscaleIpv4` accepts any up interface's IPv4 beginning `100.`, or any IPv4 on an interface
  whose name or description contains "Tailscale" (`McpHttpListener.cs:107-122`).
- **A pending offer is the expected first state.** Nothing is registered until a human accepts in
  the dashboard, and DOCA sends no push when they do — the 8 s poll is what flips the UI to
  Registered. "MCP waiting accept" in the status bar is correct, not stuck.
- **Tests that return early are not tests that fail.** See above; the inverse is also true — a
  green suite on a bare machine has proved less than it looks like it has.
- **Screen capture and the clipboard tools are Windows-only and need a real session.**
  `DocaDesk.Capture` calls `Windows.Graphics.Capture` with a `PrintWindow`/GDI fallback;
  `get_clipboard_text` / `set_clipboard_text` go through
  `Windows.ApplicationModel.DataTransfer.Clipboard`. On a headless host the capture tests return
  early rather than fail.
- **A screenshot is MCP image content** (`McpToolResult.ImageBytes`, since 2026-10-07; hub audit cl 3): the hub
  now keeps image parts (`modules/mcp/content.js`). This paragraph used to forbid exactly that, from when
  `client.js` flattened a non-text block to `[image]`.
- **A missing `npx` is one failed row, not a broken app.** DocaDesk shells out to nothing except
  the MCP servers a user defined; `McpStdioClient` turns a `Win32Exception` into
  `"<command>: not found"` (`:216-219`) and the panel draws it. No MCP server ships with the app,
  so **an empty "MCP servers on this PC" list and an empty audit log are correct first-run
  states.**
- WebView2's Evergreen runtime is a separate install. Missing it renders as "WebView2 unavailable"
  with the exception message (`MainWindow.xaml.cs:289-296`), not a blank window.
- Unpair clears the local token only — the protocol does not let a device revoke itself
  (`.agent/M1_M7_COMPLETE.md:31`). Revocation happens in the dashboard.

## Future goals

**The roadmap for this repo is not in this repo.** It is
`d:\doca\doca\docs\proposals\hub-any-client-any-mcp.md`, which is explicitly *a proposal
awaiting approval — nothing in it is implemented*. Read it before proposing architecture; it
already contains the answers to most of the obvious questions.

Its §8 gives DocaDesk exactly three items:

1. **Report `host` in its offer.** §4.3 wants a server record to carry
   `host: { kind: "device", deviceId, name }` so the agent can answer "on which machine?" without
   a lookup. Today the offer sends `label`, `url`, `headers`, `tools`, `note` (`McpOfferRequest`)
   and DOCA derives the machine from `origin.deviceId` on its own side.
2. **A `screenshot`-to-`media` path so the harness can send a desktop capture to the phone.** The
   upload half already exists — `ScreenshotTool` returns a media id and URL as text. What does not
   exist is the hub-side fan-out.
3. **An agent console, once `/api/v1/agent/*` exists** (§4.2, phase 1), so the desktop stops
   needing the WebView for chat.

Of the proposal's phase table (§7), the ones that gate work here are 1 (`/api/v1/agent/*` plus the
`harness:*` scopes), 3 and 4 (`GET /api/v1/mcp` and `mcp:control` — DocaDesk is the machine a voice
command would start a server on), and 5 (`host` on server records). Phase 8's inverted MCP
transport is for the phone and watch: DocaDesk already hosts, which is option A in §2 and the one
case the proposal calls implemented.

The project-wide convention keeps the three kinds of "not done yet" apart:

- A **deferred rough edge** — small, independent, honestly not worth doing yet — goes in
  `TODO.md`.
- A **planned feature** is a numbered phase in that proposal, not a `TODO.md` entry.
- Code that exists **because of** a future phase carries a **`// FUTURE(phase N): …`** comment, so
  one grep finds every one of them. **There are none in this repo today** — grep verified. The
  first person to write speculative code for a phase writes the first one.
