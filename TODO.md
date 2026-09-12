# TODO

Known rough edges, deliberately deferred. Each one is small and independent — none of them break
anything today. Anything that is a *planned feature* rather than a rough edge is a numbered phase
in `d:\doca\doca\DOCA\docs\proposals\hub-any-client-any-mcp.md`, not an entry here.

## Local MCP servers, deliberately minimal in the first pass

- **A definition can be added and removed, never edited.** `LocalMcpRegistry` exposes `Add`
  (`:94`), `RemoveAsync` (`:120`), `SetConsent` (`:132`) and `SetAutoStart` (`:147`) — and no
  update of any kind. The panel matches: an Add form and a **Remove** button per row
  (`MainWindow.xaml:92-97`, `MainWindow.xaml.cs:173-179`). Fixing a typo in one argument means
  removing the row and typing the whole thing again, which also silently drops its consent and
  autostart flags. Reordering is not possible either: `List()` sorts by id
  (`LocalMcpRegistry.cs:81`), so the only way to change the order in the panel is to rename a
  server. An `Update(spec)` that keeps the id and re-spawns a running server is a small method; it
  was left out because a wrong command line is visible in the row and retyping it costs seconds.

- **The Add form cannot set a working directory or a label.** `LocalMcpServerSpec` carries both
  (`:18`, `:21`), both are persisted, and both are honoured — `McpStdioClient` passes the directory
  straight to `ProcessStartInfo` (`:202`). But the form has three boxes (Id, Command, Arguments) and
  sets `Label = id` (`MainWindow.xaml.cs:196-203`), so a server that must run inside a project
  folder can only be given one by hand-editing `%LOCALAPPDATA%\DocaDesk\mcp-servers.json`. Two more
  `TextBox`es and one line each.

- **No environment support, so a server that needs an API key cannot be run at all.** This one is a
  decision rather than an omission — `client.js` accepts `env` (`modules/mcp/client.js:57`) and
  `LocalMcpServerSpec` deliberately does not, because the store is plain JSON on disk and an
  environment block is exactly where the key would go. The cost is real and worth naming: the whole
  `server-github`-shaped class of MCP server is out of reach on this machine. The right fix is not
  an `Env` dictionary; it is a per-server secret in the DPAPI store that `Spawn()` injects, which
  is more design than everything else on this list combined.

- **An argument lands in the audit log verbatim.** `mcp.local.add` records
  `$"{id}: {stored.Command} {string.Join(' ', stored.Args)}"` (`LocalMcpRegistry.cs:116`) and
  `AuditLog.Add` writes it to `audit.jsonl` without going through `RedactingLogger`. Because there
  is no environment block (above), an argument is the only place a user *can* put a token, which
  makes this the likeliest route to one sitting in cleartext. Deliberately not fixed by redacting
  the audit log — brief §6.1 is explicit that the log must not be redacted into uselessness. The
  honest fix is the DPAPI secret above, which removes the reason to type one as an argument.

- **Consent is per server; a forwarded tool has no switch of its own.** `SetConsent` registers every
  tool of a server with one flag (`LocalMcpRegistry.cs:265`) and the row draws one "Allow its tools"
  checkbox (`MainWindow.xaml.cs:136-137`), while each of the five desk tools has its own toggle. So
  a filesystem server is all-or-nothing here: you cannot allow `read_file` and refuse `write_file`.
  The machinery already exists — `ToolConsent.Register` takes a name and a bool, and the listener
  checks per name on every call — so this is a UI expansion, not a model change. It is survivable
  because DOCA has its own per-tool switches in the ⚙ panel, which is the second of the two gates
  brief §5.5 asks for; the desktop simply cannot be the first one at tool granularity.

- **A changed tool list is never re-offered to the host.** `ToolsChanged` fires on start, stop,
  remove and consent (`LocalMcpRegistry.cs:70`) and `McpHost` wires it to `Changed` (`:44`), which
  refreshes this app's own window and nothing else. The `tools` array in the offer
  (`OfferedToolNames()`, `:338`) is a snapshot of the moment the offer was made, so a server
  started later is missing from it and a removed one lingers. In practice it costs nothing
  functional: the host discovers tools by calling `tools/list`, which `DynamicTools` answers live,
  and DOCA counts only running servers anyway (`modules/mcp/tools.js:33-34`). The stale array is
  cosmetic — it is what the person reading the offer card sees. It also cannot be fixed from this
  side alone: `PATCH /mcp/self` accepts `url` and `headers` only
  (`.agent/DOCA_DESK_ADDENDUM_MCP.md`, §2), so no route would take a new tool list.

- **`AnyToolConsented()` cannot see a consented local server while the listener is off.** It is
  `ToolNames.Any(consented) || _localServers.Tools().Count > 0` (`McpHost.cs:93`), and `Tools()`
  returns only *running* servers — but autostart happens after the listener starts (`:125`). So a
  machine set up to forward one filesystem server and to consent to none of the five desk tools
  refuses a host-requested `start` with "no tools have consent" (`:310-316`), which is the exact
  opposite of what the comment above the method describes. It is narrow: it needs `McpAutoStart`
  true with the listener not actually running, which is what a start that threw for want of
  Tailscale leaves behind. The fix is to ask the *specs* whether any is consented rather than
  asking the running clients.

- **Turning the listener off leaves the local servers running.** `McpHost.StopAsync` stops the wait
  poll, clears `McpAutoStart`, resets `Registration` and calls `StopListenerOnlyAsync`
  (`:130-138`) — it never calls `_localServers.StopAllAsync()`. Only `DisposeAsync` does (`:374`),
  so those child processes survive until the app quits, reachable by nobody. Arguably right (a user
  who stopped the listener may not have meant to kill a slow-starting server) but nobody decided
  it, and the master switch reads like a kill switch, which under §2 rule 3 of the brief is the
  kind of gap that matters.

- **No HTTP transport for a local server.** DOCA's client speaks both stdio and http, including
  `Mcp-Session-Id` and the single-SSE-frame framing (`modules/mcp/client.js:41,121-148`);
  `McpStdioClient` speaks stdio only and `LocalMcpServerSpec` has no `Transport` or `Url`. An MCP
  server already listening on a port on this machine therefore cannot be forwarded — the user would
  have to hand its URL to DOCA directly, which works but loses the per-server consent gate and the
  audit line. Adding it means a second client class behind the same `IMcpTool` surface, and none of
  the servers anybody has wanted here so far are HTTP ones.

- **A concurrent second `StartAsync` for the same id can orphan a child process.**
  `LocalMcpRegistry.StartAsync` returns early only when the existing client is already `Running`
  (`:176`); one still in `Starting` is overwritten in `_clients` without being disposed, and its
  child then cannot be reached or killed. Not reachable through the UI, which disables the row's
  button for the duration (`MainWindow.xaml.cs:143-159`), and `StartAutoStartAsync` is sequential
  (`:209-213`) — but the registry is a public class with no re-entrancy guard, so a second caller is
  a matter of time.

## The listener and its address

- **`100.` is checked as a string prefix, not as a range.** Both `FindTailscaleIpv4`
  (`McpHttpListener.cs:117`) and `IsRemoteAllowed` (`:215`) accept any address whose text starts
  with `100.`, while the tailnet is `100.64.0.0/10` — so `100.0.0.0`–`100.63.255.255`, which is
  ordinary public space, passes both. It has never mattered, because the listener also requires the
  secret path and, once armed, the bearer, and those addresses do not appear on a normal desktop's
  interfaces. Whenever somebody is next in the file, a `100.64.0.0/10` containment check is a
  one-line replacement.

- **`AllowedRemoteHost` is resolved with a blocking `Dns.GetHostAddresses` inside the request
  path**, with every failure swallowed (`McpHttpListener.cs:204-213`). One lookup per request on a
  tailnet is cheap, and falling through to the `100.` check means a resolver hiccup does not lock
  the host out — which is why this has never shown up as latency. It is still a synchronous network
  call in a handler, and it will be the first thing to notice if the listener ever serves more than
  one caller.

- **Bearer enforcement arms on the host *record*, not on a host *connection*.**
  `ReconcileRegistrationAsync` flips `EnforceBearer` the moment `GET`/`PATCH /mcp/self` answers with
  an `Authorization` key in `headers` (`McpHost.cs:225-231`). The addendum report names this as the
  one piece of optional hardening left (`.agent/M2_10_MCP_ADDENDUM_REPORT.md`, §5 "Bearer"): if
  DOCA ever redacts header *names* as well as values, enforcement would never arm, and if it
  reports a name it is not actually sending, we lock out a working entry. Waiting for a successful
  authenticated request instead needs the listener to report a first success back up to `McpHost`,
  which is a channel that does not exist yet.

- **A host-requested `stop` has never been seen to reach the listener.** Live acceptance check 4
  came back **Partial**: after the dashboard's stop action the local listener still answered HTTP
  200 and the push `stop` was not observed (`.agent/LIVE_ACCEPTANCE_2_10_MCP.md`, results table).
  The handler exists and reads correctly (`McpHost.cs:288-297`), so this is an unverified path
  rather than a known break — but nobody has watched it work, and `OnPushEventAsync` has no test at
  all (see below).

## Test and build ergonomics

- **Three of the five projects are not in the solution.** `dotnet sln list` returns
  `src\DocaDesk.Capture` and `src\DocaDesk` only; `DocaDesk.Core`, `DocaDesk.Mcp` and
  `tests\DocaDesk.Tests` are reached through `ProjectReference` alone. The practical damage is that
  **`dotnet test` at the repo root builds nothing, runs nothing and exits 0** — a green command
  that tested your code not at all. Three `dotnet sln add` calls fix it; nobody noticed because
  everybody types the project path.

- **A test that cannot run reports as passed.** Every guard in the suite is a bare `return`:
  `NodeAvailable()` (`LocalMcpRegistryTests.cs:339`), a missing `client.js`
  (`McpDocaClientHandshakeTests.cs:15-19`), `Windows.Graphics.Capture` unsupported
  (`GraphicsCaptureGrabberTests.cs:12-15`), non-Windows DPAPI (`CoreBehaviorTests.cs:82`), unset
  `DOCADESK_E2E_*` (`LiveSmokeTests.cs:20-23`). So `Skipped: 0` in the summary is true and
  meaningless, and on a machine without node the local-MCP feature loses most of its coverage with
  no sign of it in the output. xUnit has had `Assert.Skip` since 2.9 and the project is on 2.9.2;
  using it would make the summary honest.

- **`McpToolsIntegrationTests`' token fallback resolves one directory too high.**
  `Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".agent", "e2e-token.json")`
  (`:22-25`) lands on `D:\doca\DocaDesk\tests\.agent\e2e-token.json`; the file is at
  `D:\doca\DocaDesk\.agent\e2e-token.json`. Five `..` are needed, not four, so the test runs only
  from the environment variables. That is arguably the better behaviour — a live test that silently
  arms itself from a token file left behind by an acceptance run is a bad default — so the fix is
  probably to delete the fallback rather than to correct the path.

- **`Loopback_refused_when_AllowLoopback_false` tests a copy of the rule, not the rule.** The test
  reimplements the allow-check inline against a bare `SimpleHttpServer`
  (`McpListenerAuthTests.cs:69-78`), because `StartLoopbackForTestsAsync` sets
  `AllowLoopback = true` as a side effect and leaves no way to reach the real `IsRemoteAllowed` with
  it false. Its own comments say so. Making `IsRemoteAllowed` `internal` with `InternalsVisibleTo`,
  or giving the test start-helper an `allowLoopback` parameter, would put the shipping code under
  test — which matters more here than in most places, since this is one of the three things holding
  the listener shut.

- **`McpHost` is untested.** The offer/accept lifecycle, `offerOn404`, the wait poll, the two
  `mcp.listener` refusals and `RegenerateSecretAsync` have no coverage, because the class lives in
  `src\DocaDesk` and depends on `AppSession`, `AppPrefs` (HKCU) and WinUI-adjacent types that the
  test project does not reference. It is also the logic most likely to be reasoned about wrongly by
  the next reader. Moving it into `DocaDesk.Mcp` behind interfaces for "the DOCA client" and "the
  prefs" is the shape of the fix; it was not worth the churn while the flow was still being
  confirmed against a live host.

## `README.md`

- **The README does not know that DocaDesk forwards MCP servers.** Its second job is described as
  giving the harness "window lists, screenshots, clipboard, and whatever else gets enabled"
  (`README.md:12-15`) — written before `LocalMcpRegistry` existed, when the five desk tools were
  the whole MCP story. Forwarding servers that run on this machine is now the larger half of it.
  The **Status** block still reads "Milestones **M1–M7 complete**" and lists
  `.agent/M1_REPORT.md … M1_M7_COMPLETE.md` (`:22-23`) with no mention of
  `.agent/DOCA_DESK_ADDENDUM_MCP.md` or the two reports that came after it, and **Specification**
  (`:27`) names only the brief, when the addendum is what is current on MCP registration.

- **Both of the README's build commands are the ones that do not work.** `dotnet test` (`:35`) runs
  zero tests, for the solution-membership reason above, and
  `dotnet build src/DocaDesk/DocaDesk.csproj -p:Platform=x64` (`:36`) fails with `MSB3027` whenever
  the app it just built is still running — which on a developer's machine it usually is.
  `dotnet test tests\DocaDesk.Tests` and a `-p:BaseOutputPath=` for the app are what actually work,
  and neither is written down anywhere except `AGENTS.md`.
