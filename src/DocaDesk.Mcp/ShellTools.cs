using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>
/// The `shell` family: <c>shell</c> and <c>shell_job</c>, with the **host's own names and
/// arguments** (DOCA `modules/harness/toolbox/files.js:39-100`) — design §1 wants one set of names
/// on every machine, and the host already had these. DOCA exposes ours as
/// `mcp__&lt;server&gt;__shell`, so they never collide with the host's.
///
/// Text results, like the host's: `exit N` then the output. A non-zero exit is an answer, not an
/// error; only a command that could not run at all is `isError`.
///
/// The shell is named in the description, at call time, for the host's reason: a description that
/// says "sh" on Windows teaches the model to write `&amp;&amp;` into PowerShell 5 and then to be
/// puzzled by the parse error.
/// </summary>
public static class ShellTools
{
    /// <summary>
    /// Below the listener's 120 s <c>ToolCallTimeout</c>, so a slow command comes back as our own
    /// "timed out, use background" answer rather than the listener's generic timeout.
    /// </summary>
    public const int LimitSec = 110;

    // ponytail: one job table per process — a listener restart must not lose track of running jobs.
    private static readonly ShellJobs Jobs = new();

    public static IReadOnlyList<FamilyTool> Create(FamilyConsent consent, AuditLog? audit) =>
    [
        new FamilyTool(ToolFamilies.Shell, new ShellTool(Jobs, audit), consent),
        new FamilyTool(ToolFamilies.Shell, new ShellJobTool(Jobs, audit), consent),
    ];

    /// <summary>On quit: a background job nobody can see or stop any more is worse than a lost one.</summary>
    public static void StopAllJobs() => Jobs.StopAll();

    internal static string Label => OperatingSystem.IsWindows() ? "Windows PowerShell 5.1" : "/bin/sh";

    /// <summary>The process that runs one command line in this machine's shell.</summary>
    internal static ProcessStartInfo StartInfo(string command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,     // closed at once: a command that reads stdin gets EOF, not a hang
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "powershell.exe";
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command" })
                psi.ArgumentList.Add(a);
            // PowerShell 5 writes the console code page unless told otherwise, and the text is
            // decoded as UTF-8 above — without this, any accent in a path comes back as mojibake.
            psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return psi;
    }

    internal static string ResolveCwd(string? cwd)
    {
        var dir = string.IsNullOrWhiteSpace(cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(cwd);
        // Process.Start reports a missing directory as "file not found", naming the shell — the
        // same misattribution D-5 fixed for local servers. Say what is actually wrong.
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"No such folder: {dir}");
        return dir;
    }

    /// <summary>Head and tail of long output: the start says what ran, the end says how it ended.</summary>
    internal static string Clip(string s, int max = 16000) =>
        s.Length <= max ? s : s[..(max / 4)] + $"\n… [{s.Length - max} characters cut] …\n" + s[^(max * 3 / 4)..];

    internal static string Str(JsonNode? args, string key) => args?[key]?.GetValue<string>() ?? "";
}

file sealed class ShellTool(ShellJobs jobs, AuditLog? audit) : IMcpTool
{
    public string Name => "shell";

    public string Description =>
        $"Run a command line on this machine and return its combined output. This machine runs {ShellTools.Label}"
        + (OperatingSystem.IsWindows() ? " — use PowerShell syntax (`;` not `&&`). " : ". ")
        + $"A call waits at most {ShellTools.LimitSec} s (timeoutSec lowers it). For anything longer, pass "
        + "background: true: it returns a job id at once; follow it with shell_job.";

    public bool ReadOnlyHint => false;

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = $"The command line, in {ShellTools.Label} syntax." },
            ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "Working directory. Defaults to the user's home folder." },
            ["timeoutSec"] = new JsonObject { ["type"] = "integer", ["description"] = $"Seconds to wait, up to {ShellTools.LimitSec}." },
            ["background"] = new JsonObject { ["type"] = "boolean", ["description"] = "Run as a background job and return its id at once." },
        },
        ["required"] = new JsonArray("command"),
    };

    public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var command = ShellTools.Str(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return new McpToolResult { IsError = true, Text = "command is required." };

        string cwd;
        try { cwd = ShellTools.ResolveCwd(ShellTools.Str(args, "cwd")); }
        catch (Exception ex) { return new McpToolResult { IsError = true, Text = ex.Message }; }

        audit?.Add("shell", command, sessionId, detail: cwd);

        if (args?["background"]?.GetValue<bool>() == true)
        {
            try
            {
                var j = jobs.Start(command, cwd);
                return new McpToolResult
                {
                    Text = $"Started background job {j.Id} (pid {j.Pid}). It keeps running after this call; "
                         + "shell_job with action status or output tells you how it stands.",
                };
            }
            catch (Exception ex) { return new McpToolResult { IsError = true, Text = ex.Message }; }
        }

        var sec = Math.Clamp(args?["timeoutSec"]?.GetValue<int>() ?? ShellTools.LimitSec, 1, ShellTools.LimitSec);
        Process p;
        try { p = Process.Start(ShellTools.StartInfo(command, cwd)) ?? throw new InvalidOperationException("the shell did not start"); }
        catch (Exception ex) { return new McpToolResult { IsError = true, Text = $"Could not start {ShellTools.Label}: {ex.Message}" }; }

        using (p)
        {
            p.StandardInput.Close();
            var output = new StringBuilder();
            var gate = new object();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(sec));
            try
            {
                await p.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                p.WaitForExit();   // drains the async readers; WaitForExitAsync does not
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                string partial;
                lock (gate) partial = output.ToString();
                return new McpToolResult
                {
                    Text = $"Timed out after {sec}s and was stopped. For long commands use background: true.\n{ShellTools.Clip(partial)}",
                };
            }

            string text;
            lock (gate) text = output.ToString().TrimEnd();
            return new McpToolResult { Text = ShellTools.Clip($"exit {p.ExitCode}\n{(text.Length == 0 ? "(no output)" : text)}") };
        }
    }
}

file sealed class ShellJobTool(ShellJobs jobs, AuditLog? audit) : IMcpTool
{
    public string Name => "shell_job";
    public string Description =>
        "Background jobs started with shell background: true. status: running or exited (with its exit code) or stopped. "
        + "output: the end of what it printed. stop: end it and everything it started. list: every job since DocaDesk started.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("status", "output", "stop", "list") },
            ["id"] = new JsonObject { ["type"] = "string", ["description"] = "The job id, for status, output and stop." },
            ["bytes"] = new JsonObject { ["type"] = "integer", ["description"] = "For output: how much from the end (default 8000)." },
        },
        ["required"] = new JsonArray("action"),
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var action = ShellTools.Str(args, "action");
        if (action == "list")
            return Done(jobs.All().Count == 0 ? "No background jobs." : string.Join('\n', jobs.All().Select(j => j.Line())));

        var id = ShellTools.Str(args, "id");
        var job = jobs.Get(id);
        if (job is null) return Fail($"No background job '{id}'. shell_job list shows them.");

        switch (action)
        {
            case "status": return Done(job.Line());
            case "output": return Done(job.Tail(args?["bytes"]?.GetValue<int>() ?? 8000));
            case "stop":
                audit?.Add("shell.job.stop", job.Id, sessionId);
                job.Stop();
                return Done(job.Line());
            default: return Fail("action is one of: status, output, stop, list.");
        }
    }

    private static Task<McpToolResult> Done(string t) => Task.FromResult(new McpToolResult { Text = t });
    private static Task<McpToolResult> Fail(string t) => Task.FromResult(new McpToolResult { IsError = true, Text = t });
}

/// <summary>
/// Background jobs for this process's lifetime. Output goes to a log file, not memory, so a chatty
/// build cannot grow the app without bound. Jobs do not survive a restart of DocaDesk, and the
/// processes do not either — quitting kills them (<see cref="ShellTools.StopAllJobs"/>), because an orphan nobody
/// can see is worse than a job that has to be started again.
/// </summary>
internal sealed class ShellJobs
{
    // ponytail: a cap, not a queue. The host uses the same shape (jobs.js MAX_RUNNING).
    private const int MaxRunning = 8;
    private readonly ConcurrentDictionary<string, ShellJob> _jobs = new(StringComparer.Ordinal);

    public ShellJob Start(string command, string cwd)
    {
        var running = _jobs.Values.Where(j => j.State == "running").ToList();
        if (running.Count >= MaxRunning)
            throw new InvalidOperationException($"{MaxRunning} background jobs are already running ({string.Join(", ", running.Select(j => j.Id))}). Wait for one, or stop one with shell_job.");
        var job = new ShellJob(command, cwd);
        _jobs[job.Id] = job;
        return job;
    }

    public ShellJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public IReadOnlyList<ShellJob> All() => _jobs.Values.OrderBy(j => j.StartedAt).ToList();
    public void StopAll() { foreach (var j in _jobs.Values) j.Stop(); }
}

internal sealed class ShellJob
{
    private readonly Process _p;
    private readonly string _log;
    private readonly object _gate = new();
    private bool _stopped;

    public ShellJob(string command, string cwd)
    {
        Id = "job_" + Guid.NewGuid().ToString("N")[..8];
        Command = command;
        StartedAt = DateTimeOffset.Now;
        _log = Path.Combine(Path.GetTempPath(), $"docadesk-{Id}.log");
        _p = Process.Start(ShellTools.StartInfo(command, cwd)) ?? throw new InvalidOperationException("the shell did not start");
        _p.StandardInput.Close();
        _p.EnableRaisingEvents = true;
        _p.Exited += (_, _) => EndedAt = DateTimeOffset.Now;
        _p.OutputDataReceived += (_, e) => Append(e.Data);
        _p.ErrorDataReceived += (_, e) => Append(e.Data);
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    public string Id { get; }
    public string Command { get; }
    public int Pid => _p.Id;
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }

    public string State => _stopped ? "stopped" : _p.HasExited ? "exited" : "running";

    public string Line() =>
        $"{Id} — {State}{(State == "exited" ? $" (exit {_p.ExitCode})" : "")} — started {StartedAt:o}"
        + (EndedAt is { } e ? $", ended {e:o}" : "") + $" — {(Command.Length > 120 ? Command[..120] : Command)}";

    public string Tail(int bytes)
    {
        lock (_gate)
        {
            if (!File.Exists(_log)) return "(no output yet)";
            var text = File.ReadAllText(_log);
            bytes = Math.Clamp(bytes, 1, 1 << 20);
            return text.Length <= bytes ? (text.Length == 0 ? "(no output yet)" : text) : text[^bytes..];
        }
    }

    public void Stop()
    {
        if (_p.HasExited) return;
        _stopped = true;
        try { _p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_gate) File.AppendAllText(_log, line + Environment.NewLine);
    }
}
