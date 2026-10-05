using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Core.Models;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>
/// The device-as-hands half: what the person allowed, what DOCA took back, and the `files_*`
/// shapes DOCA parses. The three assertions worth the most are the ones ISSUES.md D-16 names as
/// the traps — JSON text, a refusal as isError, and bare tool names — because each of them fails
/// *quietly* in a way that looks like a DOCA bug from the other end.
/// </summary>
public class DeviceHandsTests
{
    /* ── Consent: two facts, not one flag ────────────────── */

    [Fact]
    public void A_family_starts_unasked_which_is_not_the_same_as_refused()
    {
        var c = FamilyConsent.InMemory();
        Assert.Null(c.Granted(ToolFamilies.Files));      // never asked → the prompt is owed
        Assert.False(c.IsUsable(ToolFamilies.Files));    // and nothing runs meanwhile
    }

    [Fact]
    public void A_doca_revoke_does_not_erase_what_the_person_allowed()
    {
        var c = FamilyConsent.InMemory();
        c.SetGranted(ToolFamilies.Files, true);

        c.SetRevoked(ToolFamilies.Files, true);
        Assert.False(c.IsUsable(ToolFamilies.Files));
        Assert.True(c.Granted(ToolFamilies.Files));      // the grant is still theirs

        c.SetRevoked(ToolFamilies.Files, false);         // restore
        Assert.True(c.IsUsable(ToolFamilies.Files));
    }

    [Fact]
    public void A_restore_cannot_widen_access_the_person_never_gave()
    {
        var c = FamilyConsent.InMemory();
        c.SetRevoked(ToolFamilies.Shell, true);
        c.SetRevoked(ToolFamilies.Shell, false);         // DOCA restores a family nobody granted
        Assert.False(c.IsUsable(ToolFamilies.Shell));
    }

    [Fact]
    public void Every_family_is_reported_explicitly_including_the_ones_this_build_cannot_do()
    {
        var c = FamilyConsent.InMemory();
        c.SetGranted(ToolFamilies.Files, true);
        c.SetGranted(ToolFamilies.Device, true);         // granted, but not implemented as a family

        var body = c.ReportBody();

        // DOCA keeps only keys whose value is a boolean, so an omitted family keeps its old value.
        Assert.Equal(9, body.Count);
        foreach (var f in ToolFamilies.All) Assert.True(body.ContainsKey(f));
        Assert.True(body[ToolFamilies.Files]);
        Assert.False(body[ToolFamilies.Device]);         // never offer what we cannot serve
    }

    /* ── The files family: the shapes DOCA parses ────────── */

    [Fact]
    public async Task A_refused_call_is_an_mcp_error_not_a_json_result()
    {
        var c = FamilyConsent.InMemory();                 // files not granted
        var list = Tool(c, "files_list");

        var r = await list.CallAsync(new JsonObject { ["path"] = "." }, "test", default);

        // device-files.js:44 detects a refusal only by the "Error: " prefix client.js adds to an
        // isError result. Returned as ordinary JSON this would parse as success.
        Assert.True(r.IsError);
        Assert.False(LooksLikeJson(r.Text));
    }

    [Fact]
    public async Task The_tools_are_named_exactly_what_doca_calls()
    {
        var tools = FilesTools.Create(FamilyConsent.InMemory(), null);
        var names = tools.Select(t => t.Name).ToArray();

        // device-files.js:42 matches the name exactly, and a device's own family must not be
        // prefixed like a forwarded server's tool (<server>__<tool>).
        Assert.Equal(FilesTools.Names, names);
        Assert.All(names, n => Assert.DoesNotContain("__", n));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Listing_a_folder_answers_the_hosts_own_shape()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));

        var doc = await CallOkAsync(Granted(), "files_list", new JsonObject { ["path"] = dir });

        Assert.Equal(dir, doc.RootElement.GetProperty("path").GetString());
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);

        var file = entries.Single(e => e.GetProperty("name").GetString() == "a.txt");
        Assert.False(file.GetProperty("isDir").GetBoolean());
        Assert.Equal(5, file.GetProperty("size").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("mtime").GetString()));

        var sub = entries.Single(e => e.GetProperty("name").GetString() == "sub");
        Assert.True(sub.GetProperty("isDir").GetBoolean());
        // A folder's size is null in the host's shape (modules/files.js:63) — present, not absent.
        Assert.Equal(JsonValueKind.Null, sub.GetProperty("size").ValueKind);
    }

    [Fact]
    public async Task Text_and_bytes_round_trip_through_read_and_write()
    {
        var dir = NewDir();
        var consent = Granted();

        var textPath = Path.Combine(dir, "note.txt");
        await CallOkAsync(consent, "files_write", new JsonObject { ["path"] = textPath, ["content"] = "hello" });
        var read = await CallOkAsync(consent, "files_read", new JsonObject { ["path"] = textPath });
        Assert.Equal("hello", read.RootElement.GetProperty("content").GetString());
        Assert.Equal(5, read.RootElement.GetProperty("size").GetInt64());

        // The upload/download path: DOCA sends and expects base64 (device-files.js:69,77).
        var binPath = Path.Combine(dir, "bytes.bin");
        byte[] payload = [0, 1, 2, 250, 255];
        await CallOkAsync(consent, "files_write", new JsonObject
        {
            ["path"] = binPath,
            ["content"] = Convert.ToBase64String(payload),
            ["encoding"] = "base64",
        });
        Assert.Equal(payload, File.ReadAllBytes(binPath));

        var back = await CallOkAsync(consent, "files_read", new JsonObject { ["path"] = binPath, ["encoding"] = "base64" });
        Assert.Equal(payload, Convert.FromBase64String(back.RootElement.GetProperty("content").GetString()!));
    }

    [Fact]
    public async Task A_write_creates_the_folder_an_upload_names()
    {
        var consent = Granted();
        var path = Path.Combine(NewDir(), "not", "there", "yet.txt");

        // An upload posts "<dest>/<name>" without creating dest first, so a missing parent is the
        // normal case rather than an error.
        await CallOkAsync(consent, "files_write", new JsonObject { ["path"] = path, ["content"] = "x" });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Move_copy_mkdir_and_delete_do_what_they_say()
    {
        var dir = NewDir();
        var consent = Granted();

        await CallOkAsync(consent, "files_mkdir", new JsonObject { ["path"] = Path.Combine(dir, "d") });
        Assert.True(Directory.Exists(Path.Combine(dir, "d")));

        File.WriteAllText(Path.Combine(dir, "one.txt"), "1");
        await CallOkAsync(consent, "files_copy", new JsonObject
        {
            ["from"] = Path.Combine(dir, "one.txt"),
            ["to"] = Path.Combine(dir, "two.txt"),
        });
        Assert.True(File.Exists(Path.Combine(dir, "two.txt")));

        await CallOkAsync(consent, "files_move", new JsonObject
        {
            ["from"] = Path.Combine(dir, "two.txt"),
            ["to"] = Path.Combine(dir, "d", "three.txt"),
        });
        Assert.False(File.Exists(Path.Combine(dir, "two.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "d", "three.txt")));

        // Pasting a folder is one files_copy, so it has to recurse.
        await CallOkAsync(consent, "files_copy", new JsonObject
        {
            ["from"] = Path.Combine(dir, "d"),
            ["to"] = Path.Combine(dir, "d-copy"),
        });
        Assert.True(File.Exists(Path.Combine(dir, "d-copy", "three.txt")));

        await CallOkAsync(consent, "files_delete", new JsonObject
        {
            ["paths"] = new JsonArray(Path.Combine(dir, "one.txt"), Path.Combine(dir, "d-copy")),
        });
        Assert.False(File.Exists(Path.Combine(dir, "one.txt")));
        Assert.False(Directory.Exists(Path.Combine(dir, "d-copy")));
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_a_readable_refusal_not_a_crash()
    {
        var r = await Tool(Granted(), "files_read")
            .CallAsync(new JsonObject { ["path"] = Path.Combine(NewDir(), "ghost.txt") }, "test", default);

        // DOCA shows this message verbatim in the Files tab, so it has to say something.
        Assert.True(r.IsError);
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
    }

    /* ── device.control ──────────────────────────────────── */

    [Fact]
    public async Task Revoke_and_restore_move_docas_side_only()
    {
        var consent = FamilyConsent.InMemory();
        consent.SetGranted(ToolFamilies.Files, true);
        var hands = new DeviceHands(() => null, consent);

        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_1", Action = "revoke", Family = "files" });
        Assert.True(consent.IsRevoked(ToolFamilies.Files));
        Assert.False(consent.IsUsable(ToolFamilies.Files));
        Assert.True(consent.Granted(ToolFamilies.Files));

        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_2", Action = "restore", Family = "files" });
        Assert.True(consent.IsUsable(ToolFamilies.Files));
    }

    [Fact]
    public async Task An_ask_records_the_persons_answer()
    {
        var consent = FamilyConsent.InMemory();
        var hands = new DeviceHands(() => null, consent) { AskForFamily = (_, _) => Task.FromResult<bool?>(true) };

        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_3", Action = "ask", Family = "files" });

        Assert.True(consent.Granted(ToolFamilies.Files));
    }

    [Fact]
    public async Task The_actions_that_need_a_window_run_their_hook()
    {
        var consent = FamilyConsent.InMemory();
        bool reconnected = false, disconnected = false;
        var hands = new DeviceHands(() => null, consent)
        {
            OnReconnect = _ => { reconnected = true; return Task.CompletedTask; },
            OnDisconnect = _ => { disconnected = true; return Task.CompletedTask; },
        };

        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_4", Action = "reconnect" });
        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_5", Action = "disconnect" });

        Assert.True(reconnected);
        Assert.True(disconnected);
    }

    [Fact]
    public async Task An_action_that_throws_is_still_answered_rather_than_dropped()
    {
        var hands = new DeviceHands(() => null, FamilyConsent.InMemory())
        {
            OnReconnect = _ => throw new InvalidOperationException("no stream"),
        };

        // Unanswered is the worse outcome: DOCA's history would show it as never heard.
        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_6", Action = "reconnect" });

        // `ask` without a family is DOCA's own precondition (devices-control.js:49); handle it here
        // rather than throwing out of a push handler.
        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_7", Action = "ask" });

        // An action from a newer DOCA than this build knows.
        await hands.HandleControlAsync(new DeviceControlPayload { Id = "dc_8", Action = "teleport" });
    }

    /* ── D-19: a report must not re-trigger itself ───────── */

    [Fact]
    public void Only_the_persons_answer_raises_GrantsChanged_and_only_when_it_changes()
    {
        var c = FamilyConsent.InMemory();
        int grants = 0, changes = 0;
        c.GrantsChanged += () => grants++;
        c.Changed += () => changes++;

        c.SetGranted(ToolFamilies.Files, true);
        c.SetGranted(ToolFamilies.Files, true);          // same answer again: nothing to report
        Assert.Equal(1, grants);

        // A report writes DOCA's revocations back for all nine families. When that raised the
        // event the report listens to, one toggle fanned out without end (D-19).
        foreach (var f in ToolFamilies.All) c.SetRevoked(f, false);
        c.SetRevoked(ToolFamilies.Files, true);
        c.SetRevoked(ToolFamilies.Files, true);
        Assert.Equal(1, grants);
        Assert.Equal(2, changes);                        // the grant, and one real revoke
    }

    /* ── D-17: an ungranted family is not listed ─────────── */

    [Fact]
    public void Only_usable_families_are_offered()
    {
        var c = FamilyConsent.InMemory();
        var tools = FilesTools.Create(c, null).Concat(ShellTools.Create(c, null)).ToArray();

        Assert.Empty(FamilyTool.Offered(tools, c));

        c.SetGranted(ToolFamilies.Shell, true);
        var names = FamilyTool.Offered(tools, c).Select(t => t.Name).ToArray();
        Assert.Equal(["shell", "shell_job"], names);

        c.SetRevoked(ToolFamilies.Shell, true);
        Assert.Empty(FamilyTool.Offered(tools, c));
    }

    /* ── shell / processes ───────────────────────────────── */

    [Fact]
    public async Task Shell_answers_like_the_hosts_exit_code_then_output()
    {
        var shell = Family(ToolFamilies.Shell, "shell");
        var r = await shell.CallAsync(new JsonObject { ["command"] = "echo héllo; exit 3" }, "test", default);

        Assert.False(r.IsError, r.Text);                 // a non-zero exit is an answer, not an error
        Assert.StartsWith("exit 3", r.Text);
        Assert.Contains("héllo", r.Text);                // UTF-8 end to end, accents included
    }

    [Fact]
    public async Task A_slow_command_is_stopped_and_says_to_use_background()
    {
        var shell = Family(ToolFamilies.Shell, "shell");
        var r = await shell.CallAsync(new JsonObject { ["command"] = "Start-Sleep 30", ["timeoutSec"] = 1 }, "test", default);
        Assert.Contains("Timed out after 1s", r.Text);
    }

    [Fact]
    public async Task A_background_job_can_be_followed_and_stopped()
    {
        var c = FamilyConsent.InMemory();
        c.SetGranted(ToolFamilies.Shell, true);
        var tools = ShellTools.Create(c, null);
        var shell = tools.Single(t => t.Name == "shell");
        var job = tools.Single(t => t.Name == "shell_job");

        var started = await shell.CallAsync(new JsonObject
        {
            ["command"] = "echo first; Start-Sleep 30",
            ["background"] = true,
        }, "test", default);
        var id = System.Text.RegularExpressions.Regex.Match(started.Text, @"job_[0-9a-f]{8}").Value;
        Assert.False(string.IsNullOrEmpty(id), started.Text);

        string output = "";
        for (var i = 0; i < 50 && !output.Contains("first"); i++)
        {
            await Task.Delay(100);
            output = (await job.CallAsync(new JsonObject { ["action"] = "output", ["id"] = id }, "test", default)).Text;
        }
        Assert.Contains("first", output);

        var stopped = await job.CallAsync(new JsonObject { ["action"] = "stop", ["id"] = id }, "test", default);
        Assert.Contains("stopped", stopped.Text);
    }

    [Fact]
    public async Task Processes_can_be_listed_and_the_app_will_not_stop_itself()
    {
        var c = FamilyConsent.InMemory();
        c.SetGranted(ToolFamilies.Processes, true);
        var tools = ProcessAppTools.Create(c, null);

        var list = await tools.Single(t => t.Name == "processes_list").CallAsync(new JsonObject(), "test", default);
        Assert.False(list.IsError, list.Text);
        Assert.NotEmpty(JsonDocument.Parse(list.Text).RootElement.EnumerateArray());

        var self = await tools.Single(t => t.Name == "processes_stop")
            .CallAsync(new JsonObject { ["pid"] = Environment.ProcessId }, "test", default);
        Assert.True(self.IsError);
    }

    [Fact]
    public void Every_implemented_family_has_a_consent_sentence_and_no_tool_name_collides()
    {
        var c = FamilyConsent.InMemory();
        var names = FilesTools.Create(c, null).Concat(ShellTools.Create(c, null))
            .Concat(ProcessAppTools.Create(c, null)).Concat(WindowsTools.Create(c, null))
            .Select(t => t.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        // Bare names: a device's own family is trusted by DOCA only without "__" (tools.js:62).
        Assert.All(names, n => Assert.DoesNotContain("__", n));
        foreach (var f in ToolFamilies.Implemented) Assert.NotEqual(f, ToolFamilies.Describe(f));
    }

    [Fact]
    public void The_input_struct_is_the_size_SendInput_expects()
    {
        // SendInput rejects every call (ERROR_INVALID_PARAMETER) when cbSize is wrong, and that
        // would only show on portal as "Windows refused the input". 40 bytes on x64, 28 on x86.
        var t = typeof(WindowsTools).Assembly.GetType("DocaDesk.Mcp.Native+Input")!;
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, System.Runtime.InteropServices.Marshal.SizeOf(t));
    }

    [Fact]
    public async Task An_unknown_key_is_refused_before_anything_is_pressed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var c = FamilyConsent.InMemory();
        c.SetGranted(ToolFamilies.Input, true);
        var keys = WindowsTools.Create(c, null).Single(t => t.Name == "input_keys");

        // "ctrl+nosuchkey" must fail while resolving — before ctrl goes down and is left held.
        var r = await keys.CallAsync(new JsonObject { ["keys"] = "ctrl+nosuchkey" }, "test", default);
        Assert.True(r.IsError);
        Assert.Contains("nosuchkey", r.Text);
    }

    private static IMcpTool Family(string family, string name)
    {
        var c = FamilyConsent.InMemory();
        c.SetGranted(family, true);
        return ShellTools.Create(c, null).Concat(ProcessAppTools.Create(c, null)).Single(t => t.Name == name);
    }

    /* ── helpers ─────────────────────────────────────────── */

    private static FamilyConsent Granted()
    {
        var c = FamilyConsent.InMemory();
        c.SetGranted(ToolFamilies.Files, true);
        return c;
    }

    private static IMcpTool Tool(FamilyConsent consent, string name) =>
        FilesTools.Create(consent, null).Single(t => t.Name == name);

    private static async Task<JsonDocument> CallOkAsync(FamilyConsent consent, string name, JsonNode args)
    {
        var r = await Tool(consent, name).CallAsync(args, "test", default);
        Assert.False(r.IsError, r.Text);
        // device-files.js:45 parses every success with JSON.parse and answers 502 when it throws.
        return JsonDocument.Parse(r.Text);
    }

    private static bool LooksLikeJson(string s)
    {
        try { JsonDocument.Parse(s); return true; }
        catch { return false; }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "docadesk-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
