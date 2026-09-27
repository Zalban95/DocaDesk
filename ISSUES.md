# ISSUES

**Live defects.** Something here is wrong *now*, on a machine, and somebody has seen it.

This file is not `TODO.md`. The difference decides which file an entry goes in, and getting it
wrong is how a bug becomes a feature nobody fixes:

| | `TODO.md` | `ISSUES.md` (this file) |
|---|---|---|
| What it holds | Rough edges **left out on purpose**. A decision, with its reason. | Defects. Behaviour nobody chose. |
| Who wrote it | Whoever made the call, at the time they made it | Whoever hit it, or whoever found it in the code |
| When it leaves | When somebody decides to build the thing | When it is **fixed and verified on a real machine**, not when it is explained |

An entry that says "known, deferred, here is why" is a TODO. An entry that says "this does not do
what the UI says it does" is an issue, even if it has been there since day one, even if it is
small, and even if the person who wrote it knew.

## Rules for whoever is working in here

1. **Read this file and `TODO.md` before touching anything.** They are the only record. A fresh
   session that skips them rediscovers the same four things and wastes a day doing it.
2. **One entry per defect, with an id that never changes.** `D-n`. Referring to "the close bug" in
   a commit message is how two people fix two different things.
3. **Evidence or it is a guess.** Every entry names the file and the symbol. If it was observed
   rather than read, say who saw it and when, in their words.
4. **A fix is not a close.** Move an entry to `## Fixed` only after it has been *run* on the real
   machine and the symptom is gone. "The code now looks right" closes nothing. The container can
   build this repo but cannot run WinUI, so every close here is a Windows observation.
5. **Never delete an entry.** Fixed entries move down with the version that fixed them. The history
   is the point — three of the entries below are the *second* attempt at the same symptom.
6. **Partial fixes stay open, with a note.** D-1 is the example: the dialog and the preference
   landed, the hang did not. Marking it fixed because the visible half is done is how it comes
   back.
7. **When an issue turns out to be a deliberate decision, move it to `TODO.md`** with the reason.
   Do not silently drop it.

---

## Open

### D-1 · The X button does not reliably quit the app

- **Status:** open. Half-addressed in 2.23.0 — the close *policy* is now explicit, the *hang* is not.
- **Seen:** Al, repeatedly, through 2.22.x and still reported at 2.23.0: *"docadesk doesn't really
  close when the x is pressed to close it."*
- **Where:** `src\DocaDesk\Services\TrayHost.cs` — `OnClosing`, `Quit`; reached from
  `App.xaml.cs` via `BeforeQuit = () => Mcp.DisposeAsync().AsTask()`.
- **What 2.23.0 did:** added `AppPrefs.CloseToTray` (default **false**) and made `OnClosing` branch:
  hide to tray when set, otherwise `Quit()`. That settles what X is *supposed* to do. It does not
  make it happen.
- **Why it still hangs.** `OnClosing` sets `args.Cancel = true` **first**, then calls `Quit()`.
  `Quit()` is `async void` and awaits `BeforeQuit()` before it ever reaches `_window.Close()` and
  `Application.Current?.Exit()`. That await is:

  ```
  Mcp.DisposeAsync()
    → _localServers.DisposeAsync()
      → LocalMcpRegistry.StopAllAsync()          // LocalMcpRegistry.cs:277
        → foreach (id) await StopAsync(id)       // sequential, one at a time
  ```

  and `_callTimeout` is **120 seconds** per server (`LocalMcpRegistry.cs:65`). So a single stdio
  child that does not exit on request holds the whole quit for two minutes, and N of them hold it
  for N × 2 minutes — with the window's close already cancelled and no feedback on screen. From the
  outside that is exactly "I pressed X and nothing happened."
- **Fix shape.** Two changes, both small, and the second is the one that makes the guarantee:
  1. Bound the wait: `await Task.WhenAny(BeforeQuit(), Task.Delay(5s))`, and run `StopAllAsync`'s
     loop with `Task.WhenAll` rather than sequentially, so N servers cost one timeout, not N.
  2. Exit unconditionally afterwards. `Application.Current?.Exit()` is a request; if a foreground
     thread is stuck it is ignored. Follow it with `Environment.Exit(0)` on a short timer as the
     last resort, because a desktop app that will not close is worse than one that closes rudely.
  3. While the wait is running, the window should say so rather than appear frozen.
- **How to close it:** on portal, with at least one local MCP server running and one deliberately
  wedged (a command that ignores stdin), press X and confirm the process is gone from Task Manager
  within ~5 s. Attach the observation to this entry.
- **2026-09-27, portal — not a close.** Four X-closes (`CloseMainWindow`) of the D-15 builds took
  0.3–0.4 s each, one with `blender` running and one with the push loop deadlocked (`D-21`). None
  had a *wedged* server, which is the case this entry is about, so it stays open.

### D-2 · Turning the MCP listener off leaves the local servers running

- **Status:** open. This is the "server on and off" behaviour.
- **Seen:** Al: *"the server on and off issues."*
- **Where:** `src\DocaDesk\Services\McpHost.cs` — `StopAsync` (:133-141).
- **What happens.** `StartAsync` starts the listener **and then** the autostart servers
  (`_localServers.StartAutoStartAsync()`, :132). `StopAsync` stops the wait poll, clears
  `AppPrefs.McpAutoStart`, resets `Registration` and calls `StopListenerOnlyAsync()` — and stops
  **no local server at all**. Only `DisposeAsync` does (:372), i.e. only on quit. So the child
  processes survive the master switch, reachable by nobody, holding whatever they hold. The switch
  starts two things and stops one.
- **Why it matters beyond tidiness.** The panel's own row state comes from `StateOf(id)`, which
  reads the live client, so after a stop the UI honestly shows servers *running* under a listener
  that is *off* — which reads as a UI bug and is not one. And `AnyToolConsented()` asks the running
  clients rather than the specs (`McpHost.cs:93`), so the leftover state feeds back into the next
  start decision.
- **Fix shape.** Decide the contract and write it into `AGENTS.md`, then implement it:
  - **Symmetric** (my recommendation): `StopAsync` calls `_localServers.StopAllAsync()`. The switch
    means "this machine stops offering tools", which is what it looks like it means and what
    someone reaching for it under pressure will assume.
  - **Asymmetric**: leave them running, and say so in the UI next to the switch. Defensible only if
    somebody actually wants a warm server across a listener restart — nobody has asked.
  Either is fine. What is not fine is the current state, where nobody decided and the label implies
  the first.
- **Related, same area:** `TODO.md` → "Turning the listener off leaves the local servers running"
  recorded this as deferred. It is being promoted to an issue because Al hit it, which is the rule
  in point 7 above running in the other direction.

### D-3 · The delete confirmation exists in exactly one place, and nothing enforces it

- **Status:** open. The visible half shipped in 2.23.0; the guarantee did not.
- **Al's requirement:** MCP management *"doesn't have to delete without confirmation."*
- **Where:** the prompt is `MainWindow.xaml.cs:184-198` (a `ContentDialog` with
  `DefaultButton = Close`, correct — the safe button has focus). The thing it guards is
  `LocalMcpRegistry.RemoveAsync` (`LocalMcpRegistry.cs:163`), a plain public method with no guard
  of its own.
- **The problem is structural, not cosmetic.** A destructive operation whose only protection is one
  UI event handler is protected by convention. The next caller — a keyboard shortcut, a bulk
  "remove all stopped", a host-driven route, a test helper someone leaves wired up — gets no
  prompt, and nothing fails to compile to say so. `RemoveAsync` also stops the server and drops its
  consent and autostart flags, so an accidental call is not recoverable by re-adding: the flags are
  gone and the command line has to be retyped.
- **What is *not* wrong right now, so nobody re-investigates it:** no remote route deletes. The
  host-facing surface is `PATCH /mcp/self`, which takes `url` and `headers` only
  (`.agent\DOCA_DESK_ADDENDUM_MCP.md` §2). DOCA cannot delete a local server definition on this
  machine, and an agent cannot either. **That property must survive every future change** — if a
  removal route is ever added it needs a human at this keyboard, not a consent flag.
- **Fix shape.**
  1. Rename to `RemoveConfirmedAsync(string id, RemovalConsent consent)` — a type the caller can
     only obtain from the dialog. Make the compiler carry the rule instead of the reviewer.
  2. Write the definition to `mcp-servers.removed.json` (or a `.bak`) before deleting, so a wrong
     click costs a paste rather than a retype.
  3. Add a test that asserts no route on the listener reaches removal.
- **How to close it:** all three, plus the test.

### D-4 · 2.23.0 has not been reviewed

- **Status:** open, blocking. Al: *"2.23.0 needs a huge check and repair."*
- **What happened.** 2.23.0 was produced in a session with a different model and shipped without
  the review this repo's releases normally get. It touched, by timestamp:
  `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `LocalMcpRegistry.cs`, `McpHost.cs`,
  `TrayHost.cs`, `AppPrefs.cs`, `McpHttpListener.cs`, `McpStdioClient.cs`, plus `AGENTS.md`,
  `README.md`, `TODO.md`, `.gitignore`.
- **What the review has to answer**, in this order, because each one is cheap and the later ones
  only matter if the earlier ones pass:
  1. `dotnet build` clean, `dotnet test` green, on portal. (`DocaDesk.Core` has
     `TreatWarningsAsErrors`, so a warning there is already a failure.)
  2. Diff `McpHttpListener.cs` and `McpStdioClient.cs` against v2.22.x line by line. These are the
     two files where a mistake is a **security** mistake, not a bug: the path secret, bearer
     enforcement, the `100.` remote check, and the redaction that keeps a token out of the log.
  3. Confirm nothing it added writes a secret anywhere new. The comment in `App.xaml.cs` records
     that an earlier version wrote the listener URL — path secret and all — to
     `%LOCALAPPDATA%\DocaDesk\mcp-url.txt` in clear. Check no equivalent came back.
  4. Confirm `AppPrefs.CloseToTray` defaults to **false** on a machine that has never had the key
     (a fresh HKCU read), not just in the source.
  5. Re-read the `AGENTS.md` and `README.md` edits for claims that are not true of the code. A
     wrong sentence in `AGENTS.md` costs every future session, which is the most expensive kind of
     wrong thing in this repo.
- **Do not repair anything found here without adding it to this file first.** The point of the
  review is the list, not the patches.

- **Review done 2026-09-27 on portal.** Findings are `D-10` … `D-13`. Item by item:

  0. **The file list above is wrong, and that is the first finding.** It was taken *by timestamp*,
     and mtimes are not a diff. 2.23.0 is commit `610c383`; it touched **eight** files:
     `LocalMcpRegistry.cs`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `AppPrefs.cs`,
     `McpHost.cs`, `TrayHost.cs`, `LocalMcpRegistryTests.cs`. Six of the files named above were
     **not changed by it**: `McpHttpListener.cs`, `McpStdioClient.cs`, `AGENTS.md`, `README.md`,
     `TODO.md`, `.gitignore` (`git diff --quiet 739078c..610c383 -- <each>` → unchanged).
  1. **Build and tests: pass.** `dotnet build DocaDesk.sln` → `0 Warning(s) 0 Error(s)`;
     `dotnet test tests\DocaDesk.Tests` → `Passed: 76, Failed: 0`. `DocaDesk.Core`'s
     `TreatWarningsAsErrors` therefore had nothing to say.
  2. **The security diff is empty.** `git diff 739078c..610c383 --` for `McpHttpListener.cs` and
     `McpStdioClient.cs` produces **no output**: 2.23.0 did not touch either file, so the path
     secret, bearer enforcement, the `100.` check and the redaction were not at risk from this
     release at all. This item is discharged, and nobody should redo it. **The two files did change
     later**, in `0769f04` (D-8) — reviewed here instead, and that review is what produced `D-11`.
  3. **No secret is written anywhere new: confirmed.** Every write in `src` is `AuditLog`
     (by design, `AGENTS.md:243`), `RedactingLogger` (redacts), `FileCursorStore` (a seq number),
     `DpapiCredentialStore` (DPAPI) and `LocalMcpRegistry`'s store (no secrets — there is no `Env`).
     The `mcp-url.txt` write is gone. **But the file it used to write is still on disk with a live
     secret in it — `D-12`.**
  4. **`CloseToTray` defaults to false on a fresh key: confirmed on the real machine.**
     `HKCU\Software\DocaDesk` on portal holds `Tool.*`, `McpAutoStart`, `NotifyPrompts`,
     `NotifyAlerts` and **no `CloseToTray` value at all**, so `ReadBool("CloseToTray", false)` reads
     its fallback and X quits. This was the one item that could only be answered here, and it passes.
  5. **`AGENTS.md` / `README.md` claims: seven are false — `D-13`.**

  Two things 2.23.0 got right and should not be "fixed" back: `AnyToolConsented()` now asks the
  specs rather than the running clients (it closes a `TODO.md` entry), and `ReconcileRegistrationAsync`
  now always `PATCH`es because `GET` masks header values, so a rotated bearer is indistinguishable
  from a matching one. Both are improvements the documentation has not caught up with.

- **What still blocks closing D-4:** nothing in the review itself. It closes when `D-10` … `D-13`
  are patched and run here.

### D-10 · The dashboard's one signed-in load is spent even when it fails

- **Status:** open. Found 2026-09-27 on portal, reading the D-9 patch during the D-4 review.
- **Where:** `src\DocaDesk\Services\DashboardHost.cs` — `NavigateHome` (`:66-77`);
  `src\DocaDesk\MainWindow.xaml.cs` — `EnsureDashboardAsync` (`:351-392`), `Retry_Click` (`:447-451`).
- **What happens.** `NavigateHome` sets `_signedIn = true` *before* the navigation is known to have
  worked, so the token-bearing load is spent on an attempt, not on a success. The only caller of
  `UseDeviceToken` / `NavigateHome` is inside `if (_dashboard is null)` — built once, deliberately
  (the comment at `:356-361` explains why, and it is right). `Retry_Click` calls only
  `RefreshConnectionAsync()`, which comes back through `EnsureDashboardAsync`, where `_dashboard` is
  no longer null — so **Retry re-navigates nothing at all**. The WebView keeps whatever it last
  showed.
- **The case that hits it is the ordinary one.** Server unreachable at first paint — Tailscale
  still coming up, DOCA restarting, laptop woken on another network — burns the one header load.
  From then on the only route to a signed-in dashboard is restarting the app, and D-9's symptom is
  back: it asks for the password although the desk is paired.
- **Why it was not caught.** D-9 was written and patched on Linux, where the WinUI project cannot
  build, so the patch has never been watched failing. On portal right now Tailscale is down, which
  is precisely the state that reproduces it.
- **Fix shape.** Two small changes:
  1. Set `_signedIn` in `NavigationCompleted` on success, not in `NavigateHome` before the attempt —
     so a failed load leaves the token still spendable.
  2. Give Retry something to do: have `Retry_Click` (or the `NavigationFailed` path) call
     `_dashboard.NavigateHome()` as well as refreshing the session.
- **How to close it:** on portal, with DOCA unreachable, open DocaDesk, see "Dashboard offline",
  bring DOCA up, press **Retry**, and land on a dashboard that does not ask for the password.

### D-14 · D-8's held-open stream makes DOCA's client outlive its script, and the suite waits for ever

- **Status:** open. Found on portal 2026-09-27, in the first run of the tests `D-11` un-disarmed —
  which is the point of `D-11`: fixing the path immediately found a real defect behind it.
- **What happens.** Since D-8 the listener holds the GET event stream open when `initialize`
  announces `tools.listChanged`, and DOCA's `modules/mcp/client.js` opens that stream in `start()`.
  A held socket is a live libuv handle, so **`node` no longer exits when the script's last statement
  runs**. `Docas_own_client_can_call_a_server_this_machine_runs` ends with `console.log('OK')` and
  **no `process.exit(0)`** (`LocalMcpRegistryTests.cs:164`), so the child never exits.
- **Why it is worse than one slow test.** `RunNodeAsync` (`:438-456`) is
  `ReadToEndAsync` → `ReadToEndAsync` → `WaitForExitAsync` with **no timeout on any of the three**.
  So the test does not fail, it **hangs**, and it takes the whole run with it: measured here, the
  filtered run was still going at **5 minutes** and had to be killed, leaving an orphan `node`.
  `Doca_mcp_client_js_initialize_and_tools_list` is unaffected only because its script happens to
  call `process.exit(0)` (`McpDocaClientHandshakeTests.cs:40`) — the same accident that hid this.
- **This was latent in D-8 and could not be seen.** D-8 was built and tested on Linux against the
  portable projects, where this test returned early on the stale path (`D-11`). The two defects hid
  each other: the dead path meant nobody ran the test, and running it is what reveals the hang.
- **Evidence.** After the `D-11` path fix, on portal:
  `Doca_mcp_client_js_initialize_and_tools_list` → **Passed [168 ms]** (it was 2 ms when inert, so
  D-8's handshake *is* good against DOCA's real client); the forwarding test → no result in 300 s.
- **Fix shape.**
  1. `process.exit(0)` at the end of the forwarding script, as the handshake script already does.
  2. **Bound `RunNodeAsync`** — a `CancellationTokenSource` on the reads and the wait, and `Kill(true)`
     on timeout, returning the output captured so far. A test helper that can hang for ever is worth
     fixing on its own account; this is the second time an unbounded wait has cost a session here
     (`D-1` is the first, on quit).
- **How to close it:** both cross-repo tests green on portal with durations that show node ran, and
  a deliberately non-exiting script failing on the timeout rather than hanging.

### D-12 · A live listener path secret sits in cleartext in `mcp-url.txt`

- **Status:** open. Seen on portal 2026-09-27.
- **What happens.** An earlier version wrote the listener URL — path secret and all — to
  `%LOCALAPPDATA%\DocaDesk\mcp-url.txt`. The write was **removed**, and `App.xaml.cs:60-70` records
  why, in the right words: the secret is DPAPI-protected in the credential store and redacted out of
  every log line, and that file undid both. Nothing, however, deletes the file already written.
- **Evidence.** On portal the file is dated 13/09/2026 and holds
  `http://100.72.168.60:8742/mcp/<32-byte secret>`. `mcp.path.secret` has not been regenerated
  since, so that is the **current** secret, in a plain file, world-readable to anything running as
  this user. Until bearer enforcement arms, the path secret is the listener's whole authentication
  (`AGENTS.md:106-108`).
- **Where:** no code writes it any more — `grep -rn "mcp-url" src/` finds only the comment. The
  defect is the absence of a cleanup, not a write.
- **Fix shape.** Delete the file on startup if present (best-effort, no error if it is gone), next to
  the comment that explains it. Regenerating the secret in Settings is the user-side remedy and
  should be recommended once, but the file should not survive an upgrade either way.
- **How to close it:** on portal, launch the patched build and confirm the file is gone; regenerate
  the secret so the disclosed one is dead.
- **2026-09-27 15:18, portal — half closed.** `mcp-url.txt` existed before the first launch of the
  patched build and was gone after it. **The secret it disclosed is still the live one**
  (`GET /api/v1/mcp/self` shows the same path), so this stays open until Al presses Regenerate
  in Settings.

### D-24 · Stopping `blender` leaves `blender-mcp.exe` running while DocaDesk stays up

- **Status:** open. **Seen on portal 2026-09-27 19:10**, during the live `disconnect` test (`D-23`).
- **Evidence.** Audit: `19:10:25 mcp.local.stop blender`. A minute later `blender-mcp.exe`
  (pid 13632, started 19:10:15) and its `python.exe` child (45268) were still running; the parent
  recorded for 13632 (37096, the `uvx` DocaDesk spawned) no longer existed.
- **Where.** `McpStdioClient.StopAsync` (`:179-201`) calls `child.Kill(entireProcessTree: true)` on
  the `uvx` process, swallows any exception, and waits 4 s. The tree here is
  `uvx.exe` → `blender-mcp.exe` (uv's entry-point trampoline) → `python.exe`, and the grandchildren
  survived.
- **Why nobody saw it before.** Every earlier stop today was followed by DocaDesk *exiting*, and a
  process exit closes the child's stdin pipe — which a stdio MCP server treats as the end. So orphans
  died with the app. `disconnect` is the first path that stops a server and keeps DocaDesk running;
  the Settings **Stop** button is the second, and it probably leaks the same way (unverified).
- **Consequence.** A stopped-then-started server runs twice (the orphan still holds Blender's socket
  on 9876), and `D-2`'s intended fix — stop local servers with the listener — would leak on every
  toggle.
- **Not yet known:** whether `Kill(entireProcessTree)` threw (swallowed) or enumerated the tree
  before `uvx` had spawned its child. **Fix shape, pending that:** put each spawned server in a
  Windows **job object** with `KILL_ON_JOB_CLOSE`, so the whole tree ends with the job however deep it
  goes; and stop swallowing the kill's exception without a log line.
- **How to close it:** on portal, start and stop `blender` from Settings three times while DocaDesk
  stays open; no `blender-mcp.exe` survives any stop.

### D-18 · The other families: shell, processes, screen, input, apps, elevated

- **Status:** open. Feature, tracked as an entry at Al's instruction; closes only when run here.
- **Names.** §22.1 fixes only the `files` shapes. For the rest:
  - **`shell`** mirrors the **host's own tools exactly** — `shell {command, cwd?, timeoutSec?,
    background?}` and `shell_job {action: status|output|stop|list, id?, bytes?}`
    (DOCA `modules/harness/toolbox/files.js:39-100`) — because design §1 wants *"the same names on
    every machine so an agent learns them once"*, and the host already has names for this family.
    DOCA exposes a device tool as `mcp__<server>__shell`, so there is no clash with the host's.
  - The rest have no host counterpart, so they follow `files_*`: `processes_list/start/stop`,
    `screen_windows/capture`, `input_move/click/type/keys`, `apps_open`, `elevated_run`.
    **DOCA should adopt these into §22.1** — until it does, they are this client's proposal, and the
    Linux client must use the same names.
- **Portability.** `shell`, `processes` and `apps` are portable .NET and live in `DocaDesk.Mcp`.
  `input` (user32 `SendInput`) and `elevated` (UAC through the `runas` verb) are Windows-only but
  WinUI-free, so they live there too, behind `OperatingSystem.IsWindows()`, and the Linux client
  simply does not list them as implemented. `screen` needs `DocaDesk.Capture`, which `DocaDesk.Mcp`
  cannot reference (`net9.0` vs `net9.0-windows`), so its two tools wrap the existing
  `list_windows`/`screenshot` implementations in the app — no second capture path.
- **`elevated` asks Windows every time.** Design §2: the OS's own prompts are never bypassed. Each
  `elevated_run` is a separate `runas` launch, so UAC appears per call; declining it is a refusal the
  agent reads, not an error. Output comes back through a temp file because a `runas` process cannot
  have its streams redirected.
- **`device` and `mcp` stay unimplemented as families** for now: on the desk, `device`
  (notifications, prompts) is the push/prompt protocol that already runs, and `mcp` (forwarded
  servers) already has per-server consent. Reporting either as a granted family needs a decision
  about what the switch would *add*, which nobody has made — recorded in `TODO.md`.
- **Patch landed 2026-09-27, not run against DOCA.** `ShellTools`, `ProcessAppTools`, `WindowsTools`
  (input, elevated) in `DocaDesk.Mcp`; `DeskTools.CreateScreenFamily` in the app. Tests (on portal,
  green): shell exit code and UTF-8 output, timeout, a background job followed and stopped,
  `processes_list`, refusing to stop DocaDesk itself, the `INPUT` struct size `SendInput` checks, an
  unknown key refused before any key goes down, and no name collisions. **Not exercised by any
  test, on purpose:** `input_move/click/type/keys` (they would move this machine's real pointer)
  and `elevated_run` (a UAC prompt) — those two close only by hand.
- **How to close it:** on portal, each family granted in Settings → This device, and each tool run
  once from the harness — including one UAC prompt accepted and one declined.

---

## Fixed

*(Move entries here with the version that fixed them and the observation that proved it.)*

### D-23 · A `disconnect` from DOCA would switch the listener off for good

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **Where.** `McpHost` wires `OnDisconnect = _ => StopAsync()`. `StopAsync` is the path for the
  *person* turning the listener off: it sets `AppPrefs.McpAutoStart = false` (`McpHost.cs:202`).
- **What would happen.** One click on **Disconnect** in DOCA would clear the master switch in HKCU, so
  "stop until the person opens the app again" (design §5) would become "stop until the person finds
  the switch in Settings and turns it back on" — a host request overwriting an explicit local choice,
  which is the rule `mcp.listener` `stop` was written to respect (`StopListenerOnlyAsync`, master
  switch unchanged). It would also leave every local MCP server running (the `D-2` shape), when §5
  says disconnect stops the device's services.
- **Fix shape.** `McpHost.DisconnectAsync`: stop the wait poll, the listener, the local servers and the
  background shell jobs; leave `McpAutoStart` and every consent exactly as they are; say *"Disconnected
  by DOCA — reopen DocaDesk to reconnect"* in the status line. Relaunching restores the lot, because
  nothing persistent changed.
- **Not done, recorded in `TODO.md`:** the push stream stays up, because stopping it needs a notion of
  "opened again" on a tray app (window activation? relaunch only?) that nobody has decided. The
  consequence is visible: DOCA's `disconnected` flag clears at this device's next poll.
- **How to close it:** on portal, `disconnect` from the dashboard is acked, port 8742 stops
  listening, blender stops, `McpAutoStart` is still 1 in HKCU, and a relaunch brings all of it back.
- **Closed 2026-09-27:** Portal, 2026-09-27 19:10: `disconnect` acked in 119 ms; port 8742 stopped listening; `McpAutoStart` and `Family.files` still 1 in HKCU; after a relaunch the listener, blender and the grants report were all back. (blender's child survived the stop — that is `D-24`.)

### D-22 · `ask` never asks: nothing supplies `DeviceHands.AskForFamily`

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **What happens.** `McpHost` sets `OnReconnect`, `OnDisconnect` and `OnRefreshCaps` but not
  `AskForFamily`, so `ask` is acked *"cannot ask for <family>: no window"* and the person is never
  asked — design §5's "Ask again" does nothing on this device.
- **Fix shape.** `MainWindow.BindMcp` sets `AskForFamily` to a `ContentDialog` marshalled onto the UI
  thread: the family's sentence from `ToolFamilies.Describe`, **Allow** / **Don't allow**, and the
  safe button focused (`DefaultButton = Close`, as the delete dialog does). Closing it without an
  answer returns null — acked as "not put to the person", never guessed.
- **How to close it:** on portal, `ask files` from the dashboard shows the dialog; each answer is
  reflected in Settings → This device and in DOCA's `usable`.
- **Closed 2026-09-27:** Portal, 2026-09-27 19:08: `ask files` from DOCA showed the dialog; Al clicked Allow; acked 8 s later, "files allowed".

### D-21 · A `refresh` or `reconnect` from DOCA deadlocks the push loop (D-15 as landed)

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **Evidence.** DOCA holds the event as seq **196087** (`GET /api/v1/events?since=196086` returns
  it, with `nextSince: 196135`). The app's `event_cursor.txt` stopped at **196086, written 19:03:40**
  — the event right before it — and did not move for minutes while DOCA kept publishing. No
  `device.control` audit line, no ack in DOCA's history (`ackAt: null`). The one TCP connection to
  DOCA stays established; nothing is read from it.
- **The cycle.** `McpHost.OnPushEventAsync` awaits `DeviceHands.HandleControlAsync` *inside* the
  push engine's `OnEvent`. `refresh` runs `OnRefreshCaps` and `reconnect` runs `OnReconnect`, both
  `AppSession.RefreshConnectionAsync` → `EnsurePushAsync` → `StopPushAsync` →
  `PushEngine.DisposeAsync()`, which waits for the loop that is waiting for this handler. Neither
  side can finish.
- **Why it is worse than one hung action.** While it lasts, DocaDesk receives **no pushes at all**:
  no prompts, no alerts. And because the cursor never passes the event, it is **replayed on every
  launch for 24 h** (its TTL), deadlocking each fresh start the same way. The earlier `refresh` at
  15:23 (`dc_a8be0d71ab`, never acked) was very likely the same thing.
- **Why tests missed it.** `DeviceHandsTests` use stub hooks; the real hooks tear down the thing
  that is calling them, which only a live session shows.
- **Fix shape.** Hand a `device.control` event off from the push loop instead of running it inside
  it: `OnPushEventAsync` starts `HandleControlAsync` on its own task with `CancellationToken.None` and
  returns, so the loop advances the cursor and can be disposed by the action it just delivered. The
  ack still reports the outcome. Cost, accepted: if the app dies mid-action the event is not
  replayed — but the unacked entry stays visible in DOCA's history, which is the honest signal.
- **How to close it:** on portal, `refresh` and `reconnect` from the dashboard are each acked within
  seconds, and the cursor keeps moving afterwards.
- **Closed 2026-09-27:** Portal, 2026-09-27 19:06: relaunched on the fix, the stuck `refresh` (seq 196087) replayed, was acked ("caps and grants reported; usable: files") and the cursor moved on to 196167; a live `reconnect` at 19:07:14 was acked in 35 ms and the cursor kept moving (196196 at 19:08:00).

### D-20 · Settings → This device draws no toggles at all

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **Where.** `MainWindow.xaml.cs` — `RefreshFamilies` looks up the `Card` and `SettingDesc` styles
  with `Application.Current.Resources[…]`. Both are defined in `MainWindow.xaml`'s root
  `Grid.Resources` (`:7-19`), not in `App.xaml`, so the first lookup throws. `App`'s
  `UnhandledException` handler sets `e.Handled = true`, so the exception vanishes and the section is
  left empty — the rows were cleared a line before the throw.
- **Why it was not caught.** The WinUI project builds, and nothing in the test project can construct
  a WinUI page; the first time this code ran was in front of Al. Same silent-swallow shape as the
  clipboard failure `DeskTools.cs` documents: an exception with nowhere visible to go.
- **Fix shape.** Resolve the styles from the window's own root element, falling back to the
  application's, so a style moved between the two files keeps working.
- **How to close it:** on portal, Settings → This device shows nine rows, `files` … `elevated`
  switchable, `device` and `mcp` disabled.
- **Closed 2026-09-27:** Portal, 2026-09-27 18:05, Al: the nine rows appeared; `files` and `elevated` were switched on and off.

### D-19 · Reporting grants re-triggers itself (D-15 as committed in `63609d1`)

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **Where.** `McpHost` wires `_families.Changed += () => _ = _hands.ReportGrantsAsync()`.
  `ReportGrantsAsync` then calls `FamilyConsent.SetRevoked` for **all nine** families from DOCA's
  answer, and `SetRevoked` raises `Changed` **unconditionally**, whether or not anything changed.
- **What happens.** One report raises `Changed` nine times, each starting another report, each of
  which raises it nine more times: an unbounded fan-out of `PUT /devices/self/grants` from the first
  toggle or the first Paired. It would not stop until the app did. The unit tests could not see it —
  they use a null client, so `ReportGrantsAsync` returns before the loop.
- **Fix shape.** `FamilyConsent` raises its events only when a value actually changes, and splits
  them: `GrantsChanged` (the person's answer — the only thing worth reporting) and `Changed`
  (anything, for the UI and `NotifyToolsChanged`). `DeviceHands` subscribes to `GrantsChanged`
  itself, so the Linux client inherits the right wiring instead of copying the wrong one, and
  `McpHost` stops reporting on its own. Plus a test that a revoke does not raise `GrantsChanged`.
- **Patch landed 2026-09-27.** As in the fix shape; test
  `Only_the_persons_answer_raises_GrantsChanged_and_only_when_it_changes`. Green on portal, but the
  loop only shows with a live client, so this still closes on the audit-log observation below.
- **How to close it:** on portal, toggle `files` once and see exactly one `device.grants` line in
  the audit log.
- **Closed 2026-09-27:** Portal, 2026-09-27 18:08: toggling `files` on, `elevated` on and `elevated` off gave exactly three `device.grants` lines, one per toggle.

### D-17 · A family nobody granted is still listed to the harness

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **What happens.** `available()` (`tools.js:28-70`) exposes **every** tool of a device's running
  server to the harness, trusted or not, and never consults `devices-control.state().usable`. Only
  the Files tab checks the grant (`device-files.js:32`). So `PROTOCOL.md` §22.1's *"DOCA offers the
  harness only a family that is granted and not revoked"* and design §2's *"a tool the device would
  refuse is not shown as available"* are not true on the harness path. With D-16 as landed, this
  listener lists all seven `files_*` tools from the moment it starts; every call is refused by
  `FilesTools` until the person grants the family, but the agent sees them, picks them, and spends a
  step reading the refusal.
- **This is DOCA's defect as much as ours**, and it belongs in DOCA's `ISSUES.md` too — reported to
  Al rather than edited there from this repo. Our side can make the rule hold regardless, which is
  the right place for it anyway: the device is the one that knows what its person allowed.
- **Fix shape (ours).** List a family's tools only while `FamilyConsent.IsUsable(family)`, through
  `DynamicTools` (already asked on every list and every call), and `NotifyToolsChanged()` on every
  grant or revoke — already wired, so DOCA re-lists at once. `FilesTools`' own refusal stays as the
  second gate for the race between a list and a call.
- **Patch landed 2026-09-27, not run against DOCA.** `FamilyTool` (listing gate + call gate) and
  `FamilyTool.Offered`, composed into `DynamicTools` in `McpHost.StartAsync`. Test:
  `Only_usable_families_are_offered` (granted → listed, revoked → gone).
- **How to close it:** on portal, with `files` not granted, the MCP card in DOCA shows no `files_*`
  tool; grant it and they appear without ↺ Tools; revoke it in DOCA and they disappear again.
- **Closed 2026-09-27:** Portal, 2026-09-27: before the grant DOCA listed no `files_*`; after it, seven. `revoke files` from DOCA at 19:07:48.940 → DOCA's count 38 → 31 at 19:07:49.428; `restore` at 19:07:55.948 → 38 at 19:07:56.180. The DOCA-side half (`tools.js` listing ungranted tools) is still DOCA's to fix.

### D-16 · The Files tab cannot browse this machine

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **What is missing.** DOCA's Files tab and tree already speak a device
  (`modules/device-files.js`, `/api/devices/{id}/files/*`), and every route is one `files_*` tool
  call on the MCP server the device hosts. This listener offers five desk tools and forwards local
  servers; it offers **none** of the seven, so `reach()` refuses with
  *"does not offer files_list (an older client?)"* (`device-files.js:42`).
- **The shapes are not ours to choose** — `PROTOCOL.md` §22.1 and `device-files.js:14-20`:
  `files_list {path}` → `{path, entries:[{name,isDir,size,mtime}]}` (empty path = home),
  `files_read {path, encoding?}` → `{content, size, mtime}`, `files_write {path, content,
  encoding?}` → `{ok}`, `files_mkdir {path}`, `files_move {from,to}`, `files_copy {from,to}`,
  `files_delete {paths}` → `{ok}`.
- **Three details that will bite if they are guessed:**
  1. **The result must be JSON text**, and DOCA parses it with `JSON.parse`; anything else is a 502
     *"answered files_list with something that is not JSON"* (`device-files.js:45`).
  2. **A refusal must be an MCP error**, i.e. `isError: true`. `client.js` renders that as text
     prefixed `Error: `, which `device-files.js:44` strips and turns into a 400. A refusal returned
     as ordinary JSON would be parsed as *success*.
  3. **The tool names must be bare** — `files_list`, not `<server>__files_list`. `device-files.js:42`
     matches the name exactly, and §22.1 puts a device's own families on a different trust footing
     from anything it forwards.
- **Not a path jail.** Design §1 gives DocaDesk files *"everywhere the user can"*; the gate is the
  person's one-time grant plus DOCA's revoke, not a sandbox root. Say so in the code, so nobody
  later mistakes the absence of a jail for an oversight.
- **How to close it:** on portal, with `files` granted, browse this machine in DOCA's Files tab —
  list a folder, read a file, upload one (base64 write) and download one (base64 read) — and see a
  refusal when the grant is off.
- **Closed 2026-09-27:** Portal, 2026-09-27 19:08, through DOCA's own `/api/devices/dev_ee82362ec12f/files/*`: home listed (`C:\Users\alban`, 94 entries); in a scratch folder mkdir, a UTF-8 write, a multipart upload (base64 write), list with sizes, read, download (bytes `0,1,2,250,255` exact), delete; listing the deleted folder a readable 400; with `files` revoked the Files routes answered 403, after restore 200.

### D-15 · This device is not yet one of the harness's hands

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **What is missing.** DOCA has shipped its half (`modules/devices-control.js`,
  `modules/device-files.js`, DOCA 2.112.0/2.113.0). This device never answers:
  - it does not report what its person has allowed — `PUT /api/v1/devices/self/grants
    { grants: { files: true, shell: false, … } }` — so `state().usable` is empty on DOCA's side and
    the harness is offered **no** family from this machine, whatever it can actually do;
  - it ignores the durable `device.control` event (`PROTOCOL.md` §11.4:
    `{ id, action, family? }`, 24 h TTL) and never sends
    `POST /api/v1/devices/self/control/{id}/ack { ok, detail }`, so every action a person takes in
    Settings → Devices sits unacknowledged in `history` for ever. `refresh`, `reconnect`, `ask`,
    `disconnect`, `revoke`, `restore` are the six.
- **What DOCA does anyway, so be careful what "works" means.** `send()` closes the stream itself for
  `reconnect` and `disconnect` and ends the device's sessions for `disconnect`
  (`devices-control.js:63-65`), *"so the action holds even for a client that does not know the event
  yet"*. A `reconnect` will therefore look like it worked on a client that handles nothing. Only the
  **ack** proves this side ran it.
- **Consent.** Once per family, asked in our own UI, remembered, revocable here and from DOCA
  (design §2). The nine names are fixed by DOCA and unknown ones are dropped server-side
  (`devices-control.js:25,89`): `files, shell, processes, screen, input, apps, device, elevated,
  mcp`. `revoke`/`restore` are DOCA's side taking a family back — they must not silently rewrite
  what the person granted here, the same rule `mcp.listener` `stop` already follows
  (`AGENTS.md`: a host request is not permission to overwrite an explicit local choice).
- **Where it goes.** `DocaDesk.Core` (the two calls, the models) and `DocaDesk.Mcp` (the families and
  their consent), **WinUI-free**, because the Linux client is meant to reuse exactly this
  (design §6 step 3). Only the Settings UI belongs in `src\DocaDesk`.
- **Patch landed 2026-09-27, not yet run against a live DOCA.** `DocaDesk.Mcp`: `ToolFamilies` (the
  nine names, DOCA's order, plus `ImplementedOnWindows` — `files` only today), `FamilyConsent`
  (granted vs revoked kept apart; storage as two delegates so Linux supplies its own),
  `DeviceHands` (report, and all six actions, each acked — failures too). `DocaDesk.Core`:
  `PutDeviceGrantsAsync`, `AckDeviceControlAsync`, and the models. `src\DocaDesk`: HKCU
  `Family.<name>` as `bool?` because *absent ≠ false* — that is what drives the one-time prompt —
  a **This device** page in Settings, and `device.control` routed from `McpHost.OnPushEventAsync`.
  Grants are reported when the session becomes Paired and after every consent change.
- **Tests:** 15 in `DeviceHandsTests`, covering the two things most likely to be got wrong later —
  a revoke that must not erase the person's grant, a restore that must not widen access they never
  gave — plus "all nine reported explicitly". Green on portal.
- **How to close it:** on portal — grant `files` in Settings, see the device's row in DOCA show it
  as usable; press each of the six actions in Settings → Devices and see each one acked, with
  `disconnect` actually stopping this side rather than only DOCA's.
- **Closed 2026-09-27:** Portal, 2026-09-27 17:03–17:10 UTC, from the dashboard to `dev_ee82362ec12f`: `refresh`, `reconnect`, `revoke files`, `restore files`, `ask files`, `disconnect` — every one acked `ok: true` in DOCA's history within 11–119 ms (`ask` 8 s: Al clicking Allow). DOCA shows all nine families reported explicitly and `usable: ["files"]`. Required `D-21`, `D-22` and `D-23` on the way.

### D-13 · `AGENTS.md` and `TODO.md` describe a repo that has moved on

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **Each claim, and what is true:**
  1. **Solution membership.** `AGENTS.md:33-36` — *"`DocaDesk.sln` contains only two of the five
     projects"* — and `TODO.md:131-136` say the same. `dotnet sln list` on portal returns **all
     five**: `DocaDesk.Capture`, `DocaDesk.Core`, `DocaDesk.Mcp`, `DocaDesk`, `DocaDesk.Tests`.
  2. **The headline build warning, in both files.** `AGENTS.md:71-72` and `TODO.md:133-134` —
     *"`dotnet test` with no argument builds nothing, runs nothing, and exits 0"*. On portal bare
     `dotnet test` runs the suite: `Passed! Failed: 0, Passed: 76`. The instruction to always name
     the project is still good practice; the stated reason for it is no longer true, and a reader
     who trusts it will mis-diagnose a green run as a hollow one.
  3. **Test count.** `AGENTS.md:31,250` say 69 tests; there are **76**.
  4. **DOCA's path.** `AGENTS.md:13-15` cites `d:\doca\doca\DOCA\PROTOCOL.md`; the file is
     `d:\doca\doca\PROTOCOL.md`. The same stale prefix is what disarmed two tests — see `D-11`,
     which is this documentation error with teeth.
  5. **`mcp-url.txt`.** `AGENTS.md:235` lists it under "Where things are stored", *"written by
     `App.OnLaunched` — only on the auto-start path"*. Nothing writes it; see `D-12`.
  6. **The reconciliation path.** `AGENTS.md:200-203` — *"200 that matches means nothing to do"*.
     2.23.0 changed `ReconcileRegistrationAsync` to **always** `PATCH /mcp/self`, with a comment
     giving a good reason (`GET` masks header values, so a rotated bearer looks identical to a
     matching one). The behaviour is right; the documentation describes the version before it.
  7. **`TODO.md`'s `AnyToolConsented()` entry** (`:62-70`) was **fixed** by 2.23.0 — it now asks the
     specs, `_localServers.List().Any(s => s.Consented)`, which is exactly what the entry asked for.
     It is still listed as an open rough edge.
- **How to close it:** correct all seven, and say in `AGENTS.md` that the test count and the sln
  membership are the two claims most likely to rot, so the next reader checks rather than trusts.
- **Closed 2026-09-27:** All seven sentences corrected in `AGENTS.md` / `TODO.md` in `19c7b7b`; counts updated again as tests were added.

### D-11 · The two tests that prove DOCA can call this listener have not run since DOCA moved

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **Where:** `tests\DocaDesk.Tests\LocalMcpRegistryTests.cs:146` and
  `tests\DocaDesk.Tests\McpDocaClientHandshakeTests.cs:14` both hardcode
  `D:\doca\doca\DOCA\modules\mcp\client.js`. DOCA's repo root is `D:\doca\doca\`, so the file is at
  `D:\doca\doca\modules\mcp\client.js`. The old path does not exist.
- **Evidence.** `Test-Path "D:\doca\doca\DOCA\modules\mcp\client.js"` → `False`;
  `Test-Path "D:\doca\doca\modules\mcp\client.js"` → `True`. Run under `--filter`, the two tests
  report **Passed in 2 ms and 3 ms** — a real run spawns `node` and costs hundreds of milliseconds.
  The guard is a bare `return` (`McpDocaClientHandshakeTests.cs:15-19`), which `TODO.md` already
  names as a category: a test that cannot run reports as passed.
- **Why this one matters more than the category.** `AGENTS.md:262-264` calls
  `Docas_own_client_can_call_a_server_this_machine_runs` *"the highest value test in the suite: if
  it passes the dashboard will work"*. It has not passed — it has not run. Concretely: **D-8's
  handshake change was never checked against DOCA's real client.** `initialize` now answers
  `capabilities.tools.listChanged = true` and the listener holds a GET event stream open, and the
  one test that drives `modules/mcp/client.js` against it was inert the whole time.
- **Fix shape.** Resolve the path instead of hardcoding it: walk up from `AppContext.BaseDirectory`
  (or take `DOCA_REPO`) and probe both `doca\modules\mcp\client.js` and the old
  `doca\DOCA\modules\mcp\client.js`, so neither repo layout silently disarms the test. While in
  there, make the guard `Assert.Skip` (xUnit 2.9 has it; the project is on 2.9.2) so the summary
  stops lying — `TODO.md` asks for this and this entry is the reason it is worth the churn.
- **How to close it:** both tests green on portal with a duration that shows node ran, and
  `Skipped:` honest in the summary.
- **Closed 2026-09-27:** Portal, 2026-09-27: both cross-repo tests run under `--filter` in 184 ms and 299 ms (were 2–3 ms when inert), green.

### D-9 · The dashboard in DocaDesk asks for the password although the desk is paired

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **What happens.** DOCA 2.56.0+ (auth phase 1) signs a paired device's WebView in when the page
  load carries `Authorization: Bearer <device token>` (`modules/auth/credentials.js` `fromDevice`,
  capped by the device's scopes) and answers with its own session cookie; DocaMobile does this.
  `DashboardHost` sent no header by the M2 rule, so the desk signed in by password as well.
- **Patch.** `DashboardHost.NavigateHome()` sends the token on the **first** load of the server
  root only (`NavigateWithWebResourceRequest`), then plain navigation; `MainWindow` hands it
  `_session.Client?.Token`. M2's other halves stand: no script injection, no bridge, and the
  token goes to nothing but the configured server, once.
- **To close on portal:** build; unpair/pair or clear WebView data; the dashboard opens signed in.
- **Closed 2026-09-27:** Portal, 2026-09-27 19:05: DocaDesk closed, its WebView data folder (`DocaDesk.exe.WebView2`) deleted, relaunched — Al: the dashboard opened with no login. Pages beyond the device's cap (Logs) still ask for the password; that is DOCA's `capOf` / step-up design (`modules/auth/credentials.js:77-81`), not this entry.

### D-8 · A local server started later is invisible to DOCA until someone clicks ↺ Tools

- **Status:** fixed, unversioned build of 2026-09-27 (branch `doca-2.111-listchanged-token`). Verified on portal.
- **What happens.** `McpHttpListener` answers POST only and says `capabilities.tools = {}`, so it
  never tells DOCA its tool list changed. Starting a local server (or granting its consent) changes
  what `tools/list` returns, and DOCA keeps the old list until a person presses ↺ Tools
  (`AGENTS.md` said so). DOCA 2.90.0+ can now hear it: when `initialize` says
  `tools.listChanged`, `modules/mcp/client.js` holds the GET event stream open and re-lists on
  `notifications/tools/list_changed`.
- **Patch.** `McpHttpListener.cs`: `initialize` announces `listChanged: true`; a GET with
  `Accept: text/event-stream` on the secret path — after the same remote, path and bearer gates as
  a POST — is held open (`ServeEventsAsync`, a `: ping` every 25 s so a dead peer is noticed);
  `NotifyToolsChanged()` writes the notification to every open stream. `SimpleHttpServer.cs`:
  `HttpResponse` gained an optional `Stream` writer (default null — every existing response is
  unchanged). `McpHost.cs`: `LocalMcpRegistry.ToolsChanged` also calls `NotifyToolsChanged()`.
- **Tests.** `McpListChangedTests` (announce, stream, notify, close; wrong path refused). Run on
  Linux against the portable projects: 23/23 with the listener and registry tests.
- **To close on portal:** `dotnet test tests\DocaDesk.Tests` green; start a local server in
  DocaDesk and see its tools reach DOCA's MCP card without ↺ Tools.
- **Closed 2026-09-27:** Portal, 2026-09-27 18:13, blender stopped and started in Settings with DOCA's client holding the GET stream: DOCA's live tool count (`GET /api/v1/mcp/self`, polled every 250 ms) went 38 → 12 at 18:13:01.515 (stop logged 18:13:01.210) and 12 → 38 at 18:13:09.315 (start 18:13:08.891); DOCA's own client log shows one "the server says its tools changed; reading them again" per change. No ↺ Tools pressed. Locally, `Docas_client_relists_when_the_listener_says_the_tools_changed` drives DOCA's real `client.js` through the same path.

### D-5 · The Blender server stopped connecting, and the row never said why

- **Status:** fixed, unversioned build of 2026-09-16. Verified on portal.
- **Seen:** Al: *"uvx is not working. it used to but broke a few days ago."* The `blender` row
  cycled through `initialize timed out after 20s`, `the server stopped writing to stdout` and
  `uvx: not found`.
- **Three causes, none of them in `McpHttpListener`:**
  1. **C: has 0 bytes free.** A cold `uvx` build dies with `[Errno 28] No space left on device`
     (uv's cache and temp live on C:). That line was only ever in **Copy log**.
  2. **Wrong package.** The installed add-on is Blender Lab's (`extensions\user_default\mcp`,
     null-byte-delimited socket on 9876). Its server is **not on PyPI**. PyPI `blender-mcp` is
     ahujasid's (renamed to `mcp-for-blender` this month), which connects to 9876, gets no reply
     in its own protocol and never answers `initialize`. PyPI `blender-mcp-server` (djeada) crashes
     on import under `mcp` 2.x and speaks a third protocol anyway. The one that works, verified by
     hand against Blender 5.2.1:
     `uvx --from git+https://projects.blender.org/lab/blender_mcp.git#subdirectory=mcp blender-mcp`.
  3. **`workingDirectory` was a file** (`…\mcp\__init__.py`). `Process.Start` fails with
     "The directory name is invalid" and `McpStdioClient.Spawn` reported every `Win32Exception` as
     `<command>: not found`, so the row blamed `uvx`.
- **Patch:** `Spawn` checks the working directory before starting and says so; a failed start
  appends the server's last stderr line to `LastError`, so the row shows `Errno 28` rather than a
  bare timeout.
- **How to close it:** on portal, the `blender` row reaches *running* with the command above, and a
  deliberately bad working directory reads as a directory error.
- **2026-09-16 22:51, portal, after freeing space on C::** DocaDesk launched on the patched build and
  the audit log shows `mcp.local.start blender: 26 tool(s)`; `blender-mcp.exe` is a live child. Still
  open at that point.

### D-6 · Every hello on the push stream fails to parse, and the log fills at 1 Hz

- **Status:** fixed, unversioned build of 2026-09-16. Verified on portal.
- **Seen:** `docadesk.log`, 2026-09-16, one line a second: `SSE failed, falling back to JSON poll:
  The JSON value could not be converted to System.Nullable`1[System.Boolean]. Path: $.replay`.
- **Where:** `DocaDesk.Core\Models\Events.cs`, `HelloPayload.Replay` is `bool?`. DOCA sends a
  **count** (`PROTOCOL.md` hello example: `"replay":1`; `bus.js` replays a list). Nothing reads the
  field. So SSE never holds, and the app lives on the JSON poll.
- **Patch:** `int?`.
- **How to close it:** the log line is gone after a reconnect.
- **2026-09-16, portal:** no `$.replay` warning in `docadesk.log` since the 22:51 launch.

### D-7 · The Settings page is one undifferentiated column

- **Status:** fixed, unversioned build of 2026-09-16. Verified on portal.
- **Seen:** Al: *"can we remake the settings side, let's first of all make it decent."*
- **Where:** `MainWindow.xaml` `SettingsPanel`. The MCP listener came first, device preferences
  last behind a separate **Save settings** button that was easy to miss, and the add-server form
  was always visible under the list.
- **Patch:** sections (General / Desk tools / MCP servers / Activity) with setting cards; device
  preferences save when toggled; the server form opens from **Add server** or **Edit**.
- **Closed 2026-09-17:** Al, after using the agent against Blender and the new Settings page: *"working great"*.
