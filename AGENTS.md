# AGENTS.md

## What this is

DocaDesk is the **Windows desktop client** for a DOCA server (`../doca/DOCA`, the "OpenClaw
Dashboard" — a Node/Express app exposing a device-agnostic client API at `/api/v1`). It does three
things: it shows the real dashboard in WebView2 with a tray icon, native notifications and a
prompt window; it **hosts its own MCP server** over HTTP on the tailnet so DOCA's harness — which
runs on a Linux host — has tools that act on *this* machine; and it **forwards MCP servers running
on this Windows machine** through that same listener. The third is the newest part and the least
documented anywhere else.

DOCA is the authority for the protocol. `d:\doca\doca\DOCA\PROTOCOL.md` is normative,
`modules/mcp/client.js` is the exact code that calls this app's listener, and
`.agent/DOCA_DESK_BRIEF.md` plus `.agent/DOCA_DESK_ADDENDUM_MCP.md` are the briefs this repo was
built from — the addendum supersedes the brief **on MCP registration only**.

| Project | Contains |
|---|---|
| `src\DocaDesk` | The WinUI 3 app: window, tray, WebView2 host, prompt window, notifications, consent UI, settings, and `Services\McpHost.cs` which owns the listener's lifecycle |
| `src\DocaDesk.Core` | Protocol client: models, `DocaClient`, DPAPI credential store, push loop and cursor, error mapping, `RedactingLogger`. No UI dependencies. **`TreatWarningsAsErrors` is set for this project alone** (`Directory.Build.props:6`) |
| `src\DocaDesk.Mcp` | The MCP listener, its hand-rolled HTTP server, the stdio client for local servers, and the local-server registry. Must not reference WinUI — the listener has to be startable from a test with no window |
| `src\DocaDesk.Capture` | `Windows.Graphics.Capture` with a `PrintWindow`/GDI fallback, encoding and downscaling |
| `tests\DocaDesk.Tests` | xUnit, 69 tests |

**`DocaDesk.sln` contains only two of the five projects** — `src\DocaDesk.Capture` and
`src\DocaDesk`. `DocaDesk.Core`, `DocaDesk.Mcp` and `tests\DocaDesk.Tests` are reached through
`ProjectReference` alone and are not solution members. Verify with `dotnet sln list` before
assuming a solution-wide command touched what you think it did.

## Running / building

- `dotnet build DocaDesk.sln` builds Capture and the app; Core and Mcp come along transitively.
- **`dotnet test` with no argument builds nothing, runs nothing, and exits 0** — because the test
  project is not in the solution. Always name it: `dotnet test tests\DocaDesk.Tests`.
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

## The MCP listener (`src\DocaDesk.Mcp\McpHttpListener.cs`)

Hand-rolled JSON-RPC 2.0 over HTTP: POST in, JSON out, on `SimpleHttpServer` — a raw `TcpListener`
chosen to avoid `HttpListener`'s URL ACL requirement. It answers `initialize`, `tools/list`,
`tools/call` and `ping`, reporting `protocolVersion "2025-06-18"` and `serverInfo.name "DocaDesk"`
(`:235-239`). **That shape is dictated by `modules/mcp/client.js`, not by a spec.** Read that file
before changing anything here; it does not open a long-lived stream, so
`notifications/tools/list_changed` is never heard and a tool appearing later is invisible to the
dashboard until someone clicks **↺ Tools**.

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

## Tool consent (`ToolConsent`, same file)

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

## Host registration (`src\DocaDesk\Services\McpHost.cs`)

- **One reconciliation path**, `ReconcileRegistrationAsync` (`:163-242`):
  `GET /api/v1/mcp/self` → 404 means offer; 200 with a different URL *or* without an
  `Authorization` header key means `PATCH /mcp/self { url, headers }`; 200 that matches means
  nothing to do. `RegenerateSecretAsync` does not duplicate any of it — if the listener was
  running it restarts, and `StartAsync` reconciles (`:140-154`).
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

## Where things are stored

Everything durable lives under `%LOCALAPPDATA%\DocaDesk\`, outside the repo:

| Path | Written by |
|---|---|
| `credentials\<sha256 of key>.bin` | `DpapiCredentialStore`, DPAPI `CurrentUser` scope (`CredentialStore.cs:23-31,64-68`). Keys are `device.token`, `mcp.path.secret`, `mcp.bearer.token`, `tls.pin.sha256`, `server.url` (`:97-101`) |
| `mcp-servers.json` (+ `.bak`) | `LocalMcpRegistry.DefaultStorePath()` (`:72-75`) |
| `mcp-url.txt` | `App.OnLaunched` — **only on the auto-start path** (`App.xaml.cs:65-72`). Flipping the listener on in Settings does not write it, so a stale file is normal |
| `audit.jsonl` (+ `.1`) | `AuditLog`, appended per entry, 500 kept in memory, rotated at 2 MB (`AuditLog.cs:23-29,76-85`) |
| `event_cursor.txt` | `FileCursorStore` (`CursorAndWatchdog.cs:15-25`) |
| `tray.ico` | `TrayHost`, generated at runtime rather than checked in |

App-local booleans are HKCU `Software\DocaDesk` REG_DWORDs, not a file: `StartMinimized`,
`NotifyPrompts`, `NotifyAlerts`, `McpAutoStart`, and `Tool.<name>` per desk tool (`AppPrefs.cs`).

**The audit log is not redacted, and that is the design.** `RedactingLogger` covers `Bearer …`,
`doca_…` tokens and `/mcp/<secret>` (`RedactingLogger.cs:17-19,36-48`), but `AuditLog.Add` writes
its summary verbatim — brief §6.1 requires the log to stay useful. The consequence is recorded in
`TODO.md`: `mcp.local.add` writes a whole command line, arguments included.

## Tests (`tests\DocaDesk.Tests`)

`dotnet test tests\DocaDesk.Tests` — 69 tests, no server, no display, no API key, no installed MCP
server.

- **`LocalMcpRegistryTests` writes a real stdio MCP server as a `const string` of JavaScript
  inside the test** (`:368-426`) and spawns it with `node`. That is how the whole local-server path
  — handshake, tool list, a successful call, a tool that reports failure, one that never answers,
  one that exits mid-call, persistence across a simulated app restart — is covered with nothing
  installed. `--fail` makes the stub exit immediately to exercise the failure path.
- **`Docas_own_client_can_call_a_server_this_machine_runs` (`:128-157`) drives DOCA's real
  `modules/mcp/client.js`**: node `require`s
  `D:\doca\doca\DOCA\modules\mcp\client.js`, points an `McpClient` with `transport: 'http'` at the
  listener, and calls `stub__echo` through it — both hops in one test.
  `McpDocaClientHandshakeTests` does the same for `initialize` / `tools/list`. This is the highest
  value test in the suite: if it passes the dashboard will work, and if you only assert against
  your own expectations you will discover a handshake mismatch by hand in the UI.
- **A test that cannot run returns early, so it is reported as *passed*, not skipped.** There is no
  `Assert.Skip` anywhere; every guard is a bare `return` — `node` missing
  (`LocalMcpRegistryTests.cs:339`), the sibling DOCA repo absent
  (`McpDocaClientHandshakeTests.cs:15-19`), `Windows.Graphics.Capture` unsupported
  (`GraphicsCaptureGrabberTests.cs:12-15`), non-Windows DPAPI (`CoreBehaviorTests.cs:82`),
  `DOCADESK_E2E_*` unset (`LiveSmokeTests.cs:20-23`). `Skipped: 0` in the summary is therefore
  true and meaningless. On a machine without node, most of the local-MCP coverage evaporates
  silently. If you need to know a specific test really executed, run it under `--filter` and look
  at its duration.
- The live tests are gated on `DOCADESK_E2E_URL` / `DOCADESK_E2E_TOKEN`
  (`McpToolsIntegrationTests`, `LiveSmokeTests`). Note that the token-file fallback in
  `McpToolsIntegrationTests:22-25` resolves one directory too high and never fires — see `TODO.md`.
- The rest is `DocaDesk.Core` against `HttpListener` stubs: model leniency with unknown fields and
  unknown enum variants, the error-mapping table, cursor persistence across store instances,
  `resync`, gap-tolerant `seq`, `selectionId` idempotency and the new-id-after-`back` rule,
  credential round-trip, log redaction, the five-case certificate matrix, and a heartbeat watchdog
  on a fake clock — no test takes fifty seconds.

## Environment gotchas (not bugs)

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
- **Not paired is not broken.** `screenshot` answers "Not paired — cannot upload media." because a
  capture reaches the agent as an uploaded media id, never as image content: `client.js` flattens
  any non-text block to the literal string `[image]`, so a tool returning image bytes reaches the
  model as seven characters. Do not "fix" the tool to return an image block.
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
`d:\doca\doca\DOCA\docs\proposals\hub-any-client-any-mcp.md`, which is explicitly *a proposal
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
