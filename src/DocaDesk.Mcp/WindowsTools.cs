using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>
/// The Windows-only families that need no WinUI: `input` (user32 <c>SendInput</c>) and `elevated`
/// (UAC through the <c>runas</c> verb). Empty on any other OS, which is how the Linux client lists
/// neither — its `input` will be the desktop portal and its `elevated` polkit, both later.
/// </summary>
public static class WindowsTools
{
    public static IReadOnlyList<FamilyTool> Create(FamilyConsent consent, AuditLog? audit) =>
        !OperatingSystem.IsWindows()
            ? []
            :
            [
                new FamilyTool(ToolFamilies.Input, new MoveTool(audit), consent),
                new FamilyTool(ToolFamilies.Input, new ClickTool(audit), consent),
                new FamilyTool(ToolFamilies.Input, new TypeTool(audit), consent),
                new FamilyTool(ToolFamilies.Input, new KeysTool(audit), consent),
                new FamilyTool(ToolFamilies.Elevated, new ElevatedTool(audit), consent),
            ];

    internal static JsonObject Obj(params (string Name, string Type, string? Desc)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, t, d) in props)
            p[n] = d is null ? new JsonObject { ["type"] = t } : new JsonObject { ["type"] = t, ["description"] = d };
        return new JsonObject { ["type"] = "object", ["properties"] = p };
    }

    internal static McpToolResult Ok(string t = "ok") => new() { Text = t };
    internal static McpToolResult Err(string t) => new() { IsError = true, Text = t };
}

/* ── input ───────────────────────────────────────────────── */

/// <summary>
/// <c>SendInput</c>, and nothing cleverer. Coordinates are **physical pixels on the virtual
/// screen**: DocaDesk is per-monitor DPI aware, so <c>SetCursorPos</c> takes the same numbers a
/// full-resolution screenshot shows. Windows refuses input into an elevated window or the UAC
/// desktop from a normal process (UIPI) — that shows as a zero from <c>SendInput</c>, which is
/// reported rather than swallowed.
/// </summary>
internal static class Native
{
    public const uint InputMouse = 0, InputKeyboard = 1;
    public const uint KeyUp = 0x2, Unicode = 0x4, Extended = 0x1;
    public const uint LeftDown = 0x2, LeftUp = 0x4, RightDown = 0x8, RightUp = 0x10, MiddleDown = 0x20, MiddleUp = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeybdInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Explicit)]
    public struct Union { [FieldOffset(0)] public MouseInput Mi; [FieldOffset(0)] public KeybdInput Ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input { public uint Type; public Union U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    public static void Send(List<Input> inputs)
    {
        if (inputs.Count == 0) return;
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
            throw new InvalidOperationException(
                "Windows refused the input — the target is probably elevated or the session is locked "
                + $"(SendInput sent {sent} of {inputs.Count}, error {Marshal.GetLastWin32Error()}).");
    }

    public static Input Mouse(uint flags) => new() { Type = InputMouse, U = new Union { Mi = new MouseInput { Flags = flags } } };

    public static Input Key(ushort vk, bool up, bool extended = false) => new()
    {
        Type = InputKeyboard,
        U = new Union { Ki = new KeybdInput { Vk = vk, Flags = (up ? KeyUp : 0) | (extended ? Extended : 0) } },
    };

    public static Input Char(char c, bool up) => new()
    {
        Type = InputKeyboard,
        U = new Union { Ki = new KeybdInput { Scan = c, Flags = Unicode | (up ? KeyUp : 0) } },
    };
}

file sealed class MoveTool(AuditLog? audit) : IMcpTool
{
    public string Name => "input_move";
    public string Description => "Move the pointer to x, y in physical screen pixels (the numbers a full-size screen_capture shows).";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => WindowsTools.Obj(("x", "integer", null), ("y", "integer", null));

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        if (MeetingHold.Held) return Task.FromResult(WindowsTools.Err(MeetingHold.Refusal));
        int x = args?["x"]?.GetValue<int>() ?? 0, y = args?["y"]?.GetValue<int>() ?? 0;
        audit?.Add("input.move", $"{x},{y}", sessionId);
        return Task.FromResult(Native.SetCursorPos(x, y) ? WindowsTools.Ok() : WindowsTools.Err($"Could not move the pointer to {x},{y}."));
    }
}

file sealed class ClickTool(AuditLog? audit) : IMcpTool
{
    public string Name => "input_click";
    public string Description => "Click at x, y (physical pixels), or where the pointer is when they are left out. button: left (default), right, middle. double: true for a double-click.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => WindowsTools.Obj(("x", "integer", null), ("y", "integer", null), ("button", "string", "left | right | middle"), ("double", "boolean", null));

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        if (MeetingHold.Held) return Task.FromResult(WindowsTools.Err(MeetingHold.Refusal));
        try
        {
            if (args?["x"] is not null && args?["y"] is not null &&
                !Native.SetCursorPos(args["x"]!.GetValue<int>(), args["y"]!.GetValue<int>()))
                return Task.FromResult(WindowsTools.Err("Could not move the pointer there."));

            var (down, up) = (args?["button"]?.GetValue<string>() ?? "left") switch
            {
                "right" => (Native.RightDown, Native.RightUp),
                "middle" => (Native.MiddleDown, Native.MiddleUp),
                _ => (Native.LeftDown, Native.LeftUp),
            };
            var times = args?["double"]?.GetValue<bool>() == true ? 2 : 1;
            var inputs = new List<Native.Input>();
            for (var i = 0; i < times; i++) { inputs.Add(Native.Mouse(down)); inputs.Add(Native.Mouse(up)); }
            Native.Send(inputs);
            audit?.Add("input.click", args?.ToJsonString() ?? "", sessionId);
            return Task.FromResult(WindowsTools.Ok());
        }
        catch (Exception ex) { return Task.FromResult(WindowsTools.Err(ex.Message)); }
    }
}

file sealed class TypeTool(AuditLog? audit) : IMcpTool
{
    public string Name => "input_type";
    public string Description => "Type text into whatever has the keyboard focus, as if typed. Newlines press Enter.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => WindowsTools.Obj(("text", "string", null));

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        if (MeetingHold.Held) return Task.FromResult(WindowsTools.Err(MeetingHold.Refusal));
        var text = args?["text"]?.GetValue<string>() ?? "";
        try
        {
            var inputs = new List<Native.Input>();
            foreach (var c in text.Replace("\r\n", "\n"))
            {
                if (c == '\n') { inputs.Add(Native.Key(0x0D, false)); inputs.Add(Native.Key(0x0D, true)); }
                else { inputs.Add(Native.Char(c, false)); inputs.Add(Native.Char(c, true)); }
            }
            Native.Send(inputs);
            // The length, not the text: this is where a password typed by the agent would land.
            audit?.Add("input.type", $"{text.Length} characters", sessionId);
            return Task.FromResult(WindowsTools.Ok());
        }
        catch (Exception ex) { return Task.FromResult(WindowsTools.Err(ex.Message)); }
    }
}

file sealed class KeysTool(AuditLog? audit) : IMcpTool
{
    public string Name => "input_keys";
    public string Description => "Press a key or a combination, e.g. enter, ctrl+c, alt+tab, win+r, ctrl+shift+esc, f5. Several separated by spaces are pressed in turn.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => WindowsTools.Obj(("keys", "string", "e.g. \"ctrl+s\" or \"ctrl+a delete\""));

    private static readonly Dictionary<string, (ushort Vk, bool Ext)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = (0x11, false), ["control"] = (0x11, false), ["shift"] = (0x10, false), ["alt"] = (0x12, false),
        ["win"] = (0x5B, true), ["enter"] = (0x0D, false), ["return"] = (0x0D, false), ["esc"] = (0x1B, false),
        ["escape"] = (0x1B, false), ["tab"] = (0x09, false), ["space"] = (0x20, false), ["backspace"] = (0x08, false),
        ["delete"] = (0x2E, true), ["del"] = (0x2E, true), ["insert"] = (0x2D, true), ["home"] = (0x24, true),
        ["end"] = (0x23, true), ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true), ["up"] = (0x26, true),
        ["down"] = (0x28, true), ["left"] = (0x25, true), ["right"] = (0x27, true), ["printscreen"] = (0x2C, true),
    };

    internal static (ushort Vk, bool Ext) Resolve(string key)
    {
        if (Named.TryGetValue(key, out var k)) return k;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return (char.ToUpperInvariant(key[0]), false);
        if (key.Length is 2 or 3 && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out var n) && n is >= 1 and <= 24)
            return ((ushort)(0x70 + n - 1), false);
        throw new ArgumentException($"Unknown key '{key}'.");
    }

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        if (MeetingHold.Held) return Task.FromResult(WindowsTools.Err(MeetingHold.Refusal));
        var spec = args?["keys"]?.GetValue<string>() ?? "";
        try
        {
            var inputs = new List<Native.Input>();
            foreach (var combo in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var keys = combo.Split('+').Select(Resolve).ToList();
                foreach (var (vk, ext) in keys) inputs.Add(Native.Key(vk, false, ext));
                for (var i = keys.Count - 1; i >= 0; i--) inputs.Add(Native.Key(keys[i].Vk, true, keys[i].Ext));
            }
            if (inputs.Count == 0) return Task.FromResult(WindowsTools.Err("keys is required."));
            Native.Send(inputs);
            audit?.Add("input.keys", spec, sessionId);
            return Task.FromResult(WindowsTools.Ok());
        }
        catch (Exception ex) { return Task.FromResult(WindowsTools.Err(ex.Message)); }
    }
}

/* ── elevated ────────────────────────────────────────────── */

/// <summary>
/// One PowerShell command as administrator, **through UAC every time** — design §2: the OS's own
/// prompts are never bypassed, and the family grant is not a standing admin token. A declined
/// prompt is an answer the agent reads (`ERROR_CANCELLED`, 1223), not a failure of this app.
///
/// A <c>runas</c> launch cannot redirect its streams, so the elevated shell writes its output to a
/// file in a fresh folder and this side reads it back. The command travels as
/// <c>-EncodedCommand</c> so no quoting can change what runs; the cost is that UAC's "details" show
/// base64 rather than the command — recorded in TODO.md.
/// </summary>
file sealed class ElevatedTool(AuditLog? audit) : IMcpTool
{
    public string Name => "elevated_run";
    public string Description =>
        $"Run one Windows PowerShell 5.1 command as administrator. Windows asks the person at this machine (UAC) every time; "
        + $"if they decline, you are told. Waits at most {ShellTools.LimitSec} s. Use shell for anything that does not need admin.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "PowerShell syntax." },
            ["timeoutSec"] = new JsonObject { ["type"] = "integer" },
        },
        ["required"] = new JsonArray("command"),
    };

    public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var command = args?["command"]?.GetValue<string>() ?? "";
        if (command.Trim().Length == 0) return WindowsTools.Err("command is required.");
        var sec = Math.Clamp(args?["timeoutSec"]?.GetValue<int>() ?? ShellTools.LimitSec, 1, ShellTools.LimitSec);

        var dir = Path.Combine(Path.GetTempPath(), "docadesk-elevated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outFile = Path.Combine(dir, "out.txt");
        var script =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n"
            + $"& {{\n{command}\n}} *>&1 | Out-File -LiteralPath '{outFile.Replace("'", "''")}' -Encoding utf8 -Width 4096\n"
            + "exit $LASTEXITCODE";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        // Recorded before the prompt, in full: the audit log is where the person looks afterwards
        // to see what they said yes to, and UAC itself cannot show them.
        audit?.Add("elevated.ask", command, sessionId);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("it did not start");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(sec));
            try { await p.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Cannot kill an elevated process from here; say so rather than pretend.
                return WindowsTools.Err($"Timed out after {sec}s. The elevated command may still be running (pid {p.Id}).");
            }

            var output = File.Exists(outFile) ? (await File.ReadAllTextAsync(outFile, ct).ConfigureAwait(false)).TrimEnd() : "";
            audit?.Add("elevated.done", $"exit {p.ExitCode}", sessionId);
            return WindowsTools.Ok(ShellTools.Clip($"exit {p.ExitCode}\n{(output.Length == 0 ? "(no output)" : output)}"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            audit?.Add("elevated.declined", command, sessionId);
            return WindowsTools.Err("The person at this machine declined the Windows administrator prompt.");
        }
        catch (Exception ex)
        {
            return WindowsTools.Err($"Could not run as administrator: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
