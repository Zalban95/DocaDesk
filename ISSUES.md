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

---

## Fixed

*(Move entries here with the version that fixed them and the observation that proved it.)*

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
