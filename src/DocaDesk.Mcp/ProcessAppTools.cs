using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>
/// The `processes` family (`processes_list/start/stop`) and the `apps` family (`apps_open`).
/// Portable .NET: the Linux client gets both unchanged. Names follow `files_*`; §22.1 does not fix
/// them yet (ISSUES.md D-18), so these are this client's proposal to DOCA.
/// </summary>
public static class ProcessAppTools
{
    public static IReadOnlyList<FamilyTool> Create(FamilyConsent consent, AuditLog? audit) =>
    [
        new FamilyTool(ToolFamilies.Processes, new ListTool(), consent),
        new FamilyTool(ToolFamilies.Processes, new StartTool(audit), consent),
        new FamilyTool(ToolFamilies.Processes, new StopTool(audit), consent),
        new FamilyTool(ToolFamilies.Apps, new OpenTool(audit), consent),
    ];

    internal static string Str(JsonNode? a, string k) => a?[k]?.GetValue<string>() ?? "";
    internal static McpToolResult Ok(object o) => new() { Text = JsonSerializer.Serialize(o) };
    internal static McpToolResult Err(string t) => new() { IsError = true, Text = t };
}

file sealed class ListTool : IMcpTool
{
    public string Name => "processes_list";
    public string Description => "List running programs on this machine: pid, name, memory in MB, window title. Largest first, at most 200. filter matches the name.";
    public bool ReadOnlyHint => true;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["filter"] = new JsonObject { ["type"] = "string" } },
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var filter = ProcessAppTools.Str(args, "filter");
        var rows = new List<(long Mem, object Row)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (filter.Length > 0 && !p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    var mem = p.WorkingSet64;
                    string? title = null;
                    try { title = string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { /* not ours to read */ }
                    rows.Add((mem, new { pid = p.Id, name = p.ProcessName, memoryMb = mem / (1024 * 1024), title }));
                }
                catch { /* exited between the list and the read */ }
            }
        }
        return Task.FromResult(ProcessAppTools.Ok(rows.OrderByDescending(r => r.Mem).Take(200).Select(r => r.Row)));
    }
}

file sealed class StartTool(AuditLog? audit) : IMcpTool
{
    public string Name => "processes_start";
    public string Description => "Start a program on this machine and return its pid without waiting for it. For a command whose output you want, use shell instead.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The executable: a path, or a name on PATH." },
            ["args"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["cwd"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("command"),
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var command = ProcessAppTools.Str(args, "command");
        if (command.Length == 0) return Task.FromResult(ProcessAppTools.Err("command is required."));
        try
        {
            var psi = new ProcessStartInfo(command) { UseShellExecute = false };
            foreach (var a in (args?["args"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "") ?? [])
                psi.ArgumentList.Add(a);
            var cwd = ProcessAppTools.Str(args, "cwd");
            if (cwd.Length > 0) psi.WorkingDirectory = ShellTools.ResolveCwd(cwd);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("it did not start");
            audit?.Add("processes.start", $"{command} {string.Join(' ', psi.ArgumentList)}", sessionId, detail: $"pid={p.Id}");
            return Task.FromResult(ProcessAppTools.Ok(new { pid = p.Id }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProcessAppTools.Err($"Could not start {command}: {ex.Message}"));
        }
    }
}

file sealed class StopTool(AuditLog? audit) : IMcpTool
{
    public string Name => "processes_stop";
    public string Description => "Stop a program on this machine by pid, with everything it started (tree: false for only the one).";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pid"] = new JsonObject { ["type"] = "integer" },
            ["tree"] = new JsonObject { ["type"] = "boolean" },
        },
        ["required"] = new JsonArray("pid"),
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var pid = args?["pid"]?.GetValue<int>() ?? 0;
        // The agent's own hands: stopping DocaDesk would end the call that asked for it and leave
        // DOCA with a device that vanished mid-sentence.
        if (pid == Environment.ProcessId)
            return Task.FromResult(ProcessAppTools.Err("That is DocaDesk itself. Quit it from its window or tray instead."));
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            p.Kill(entireProcessTree: args?["tree"]?.GetValue<bool>() ?? true);
            audit?.Add("processes.stop", $"{pid} {name}", sessionId);
            return Task.FromResult(ProcessAppTools.Ok(new { ok = true }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProcessAppTools.Err($"Could not stop {pid}: {ex.Message}"));
        }
    }
}

file sealed class OpenTool(AuditLog? audit) : IMcpTool
{
    public string Name => "apps_open";
    public string Description => "Open something on this machine the way a double-click would: a file in its default app, a folder, a link, or an app by name.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["target"] = new JsonObject { ["type"] = "string", ["description"] = "A path, a URL, or an app name such as notepad." } },
        ["required"] = new JsonArray("target"),
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var target = ProcessAppTools.Str(args, "target");
        if (target.Length == 0) return Task.FromResult(ProcessAppTools.Err("target is required."));
        try
        {
            // ShellExecute on Windows; .NET maps UseShellExecute to xdg-open on Linux.
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            audit?.Add("apps.open", target, sessionId);
            return Task.FromResult(ProcessAppTools.Ok(new { ok = true }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ProcessAppTools.Err($"Could not open {target}: {ex.Message}"));
        }
    }
}
