# TODO

Known rough edges, deliberately deferred. Each one is small and independent — none of them break
anything today. Anything that is a *planned feature* rather than a rough edge is a numbered phase
in `d:\doca\doca\DOCA\docs\proposals\hub-any-client-any-mcp.md`, not an entry here.

## Local MCP servers, deliberately minimal in the first pass

- ~~**A definition can be added and removed, never edited.**~~ **Done in 2.23.0** (`610c383`):
  `LocalMcpRegistry.UpdateAsync` (`:125`) changes command, args, label and working directory,
  keeps consent and autostart, and restarts a running server; the panel has an **Edit** button per
  row wired to it (`MainWindow.xaml.cs:185-186`, `:288`). **Still true:** reordering is not possible,
  because `List()` sorts by id — renaming a server is the only way to change the panel's order.

- **The Add form cannot set a label.** *(The working-directory half is done: the form has
  `LocalMcpCwdBox`, "A folder, not a file" — `MainWindow.xaml:333`, added with D-5/D-7.)*
  `LocalMcpServerSpec` still carries a `Label` that the form never sets, so `Label = id` always and
  a row cannot be given a friendlier name than its tool prefix. One `TextBox` and one line.

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

- **A changed tool list is never re-offered to the host.** *(Half of this is now done: since D-8
  `ToolsChanged` also calls `NotifyToolsChanged()`, so DOCA re-lists and a server started later
  reaches the dashboard without ↺ Tools. What remains is the stale `tools` array on the offer card,
  below.)* `ToolsChanged` fires on start, stop,
  remove and consent (`LocalMcpRegistry.cs:70`) and `McpHost` wires it to `Changed` (`:44`), which
  refreshes this app's own window. The `tools` array in the offer
  (`OfferedToolNames()`, `:338`) is a snapshot of the moment the offer was made, so a server
  started later is missing from it and a removed one lingers. In practice it costs nothing
  functional: the host discovers tools by calling `tools/list`, which `DynamicTools` answers live,
  and DOCA counts only running servers anyway (`modules/mcp/tools.js:33-34`). The stale array is
  cosmetic — it is what the person reading the offer card sees. It also cannot be fixed from this
  side alone: `PATCH /mcp/self` accepts `url` and `headers` only
  (`.agent/DOCA_DESK_ADDENDUM_MCP.md`, §2), so no route would take a new tool list.

- ~~**`AnyToolConsented()` cannot see a consented local server while the listener is off.**~~
  **Done in 2.23.0** (`610c383`), exactly as this entry asked: it is now
  `ToolNames.Any(consented) || _localServers.List().Any(s => s.Consented)` — the *specs*, not the
  running clients. Kept per the never-delete rule; found still listed as open during the D-4 review.

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

## Devices as hands, deliberately one family at a time

- **`device` and `mcp` are not families here yet.** They are named in `ToolFamilies`, drawn
  disabled in Settings, and reported `false`. On the desk, `device` (notifications, prompts) is the
  push/prompt protocol that already runs, and `mcp` (forwarded servers) already has per-server
  consent; reporting either as a granted family needs a decision about what the switch would *add*
  — gate the prompts? all forwarded servers at once? — and nobody has made it. Harmless meanwhile:
  DOCA exposes forwarded tools whatever the `mcp` grant says (`D-17`).

- **UAC shows base64, not the command.** `elevated_run` passes the script as `-EncodedCommand` so
  no quoting can change what runs, which means UAC's "Show more details" displays an unreadable
  command line. The command is in the audit log before the prompt appears, but the person deciding
  in the UAC dialog cannot see it there. A DocaDesk-drawn "about to ask for admin to run: …" toast
  before the prompt is the fix; left out because it needs the WinUI side and a way to show it over
  a full-screen app.

- **`files_delete` bypasses the recycle bin**, and nothing is checkpointed (below). A wrong delete
  from the agent is unrecoverable. `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(…,
  RecycleOption.SendToRecycleBin)` is one call on Windows; left out because it is Windows-only and
  the Linux client would need its own trash (`gio trash`), which is a portability decision.

- **`refresh` does not re-report caps.** §22.1 says the action should report caps *and* grants
  again, via `PATCH /devices/{id}`. There is no client method for that route — caps are sent once,
  at pair time (`AppSession.PairAsync`) — so `DeviceHands.OnRefreshCaps` reopens the connection and
  re-reads the server's capabilities instead, and only the grants half is genuinely re-reported.
  The ack says what actually happened rather than claiming both. One method on `DocaClient` and one
  line here; left out because nothing reads a stale cap today.

- **A `disconnect` is not visible to the person.** `OnDisconnect` stops the listener and the local
  servers, which is right, but the window says nothing about *why* everything went quiet — it looks
  like the listener was switched off by itself. Design §5 gives the action its own meaning
  ("stop its services until it is opened again"), so a line in the status bar naming DOCA as the
  reason is worth one `RegistrationMessage`.

- **Folder checkpoints are not taken.** Design §4 wants the client to checkpoint a folder before the
  agent changes one it has not checkpointed this turn, with DOCA listing and restoring them. None of
  that exists here: `files_write`, `files_move` and `files_delete` change things with no way back
  except the recycle bin, which they also bypass (`File.Delete`, `Directory.Delete`). This is the
  largest single gap in the family and the reason to be careful about granting it on a machine that
  matters. It is a design item, not a rough edge — but it is recorded here because the `files`
  family shipped without it.

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

- ~~**Three of the five projects are not in the solution.**~~ **Done** — all five are solution
  members, and bare `dotnet test` runs the suite (`Passed: 76`, portal 2026-09-27). Kept because
  the entry is why `AGENTS.md` carried a false build warning for so long: both files went on saying
  `dotnet test` tested nothing. See `ISSUES.md` → `D-13`.

- **A test that cannot run reports as passed.** Every guard in the suite is a bare `return`:
  `NodeAvailable()`, a missing `client.js`, `Windows.Graphics.Capture` unsupported
  (`GraphicsCaptureGrabberTests.cs:12-15`), non-Windows DPAPI (`CoreBehaviorTests.cs:82`), unset
  `DOCADESK_E2E_*` (`LiveSmokeTests.cs:20-23`). So `Skipped: 0` in the summary is true and
  meaningless, and on a machine without node the local-MCP feature loses most of its coverage with
  no sign of it in the output.
  **Correction, 2026-09-27:** this entry used to say *"xUnit has had `Assert.Skip` since 2.9 and the
  project is on 2.9.2; using it would make the summary honest"*. That was tried and it does not
  compile — `Assert.SkipUnless` / `Assert.Skip` are **not** in `xunit.assert` 2.9.2
  (`error CS0117: 'Assert' does not contain a definition for 'SkipUnless'`); dynamic skip is xUnit
  **v3**. So the honest summary costs a framework migration, not a one-line change, which is why the
  guards are still returns. What did get fixed is the worse half: a guard that fired because a
  *path* had rotted rather than because the environment was genuinely missing — see `ISSUES.md`
  → `D-11`, and `DocaRepo` for the resolver.

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
