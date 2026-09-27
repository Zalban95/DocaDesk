using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>
/// The `files` family, on the MCP server this device hosts (PROTOCOL.md §22.1).
///
/// These seven back DOCA's Files tab and tree for this machine: every `/api/devices/{id}/files/*`
/// route is one call to one of them (`modules/device-files.js`). Three things about that file
/// decide the shape here, and all three fail quietly if they are guessed:
///
/// 1. <b>The result is JSON text.</b> `device-files.js:45` does `JSON.parse(text)` and answers 502
///    if it throws. So every success returns a serialised object, not a human sentence.
/// 2. <b>A refusal is an MCP error</b> — `isError: true`. `client.js` renders that with an
///    `Error: ` prefix, which `device-files.js:44` strips and turns into a 400. A refusal written
///    as ordinary JSON would be parsed as a *successful* result.
/// 3. <b>The names are bare</b> (`files_list`, never `<server>__files_list`): `device-files.js:42`
///    matches exactly, and §22.1 trusts a device's own families differently from what it forwards.
///
/// <b>There is deliberately no path jail.</b> Design §1 gives DocaDesk files "everywhere the user
/// can", and the gates are the person's one-time grant plus DOCA's revoke — not a sandbox root.
/// That is a decision, not an omission: do not add a root without reading §2 and §3 first, because
/// a root would also silently break the Files tab's machine selector.
/// </summary>
public static class FilesTools
{
    public static IReadOnlyList<IMcpTool> Create(FamilyConsent consent, AuditLog? audit) =>
    [
        new ListTool(consent, audit),
        new ReadTool(consent, audit),
        new WriteTool(consent, audit),
        new MkdirTool(consent, audit),
        new MoveTool(consent, audit),
        new CopyTool(consent, audit),
        new DeleteTool(consent, audit),
    ];

    /// <summary>The names, so consent registration and tests do not retype them.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["files_list", "files_read", "files_write", "files_mkdir", "files_move", "files_copy", "files_delete"];

    /// <summary>An empty path is the device's home (§22.1), which is also what the tree opens on.</summary>
    internal static string Resolve(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(path);

    /// <summary>
    /// Plain serialisation, deliberately not <c>DocaJson.Options</c>: that sets
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c>, which would drop the <c>size: null</c> on a
    /// folder and the <c>mtime: null</c> on an entry that could not be stat'd — both of which the
    /// host's own shape includes (`modules/files.js:60-68`). The anonymous types below already
    /// spell their properties exactly as DOCA expects, so no naming policy is wanted either.
    /// </summary>
    internal static string Json(object o) => JsonSerializer.Serialize(o);
}

/// <summary>
/// Shared plumbing: the grant check, the audit line, and turning an exception into the refusal
/// shape rather than letting it escape as a JSON-RPC -32603 that DOCA reports as a 500.
/// </summary>
file abstract class FilesToolBase : IMcpTool
{
    private readonly FamilyConsent _consent;
    private readonly AuditLog? _audit;

    protected FilesToolBase(FamilyConsent consent, AuditLog? audit)
    {
        _consent = consent;
        _audit = audit;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool ReadOnlyHint { get; }
    public abstract JsonObject InputSchema { get; }

    protected abstract object Run(JsonNode? args);

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        if (!_consent.IsUsable(ToolFamilies.Files))
        {
            _audit?.Add("files.denied", Name, sessionId);
            // isError, so DOCA reads it as a refusal (400) rather than parsing it as a result.
            return Task.FromResult(new McpToolResult
            {
                IsError = true,
                Text = _consent.IsRevoked(ToolFamilies.Files)
                    ? "Files on this machine were revoked in DOCA."
                    : "Files on this machine are not allowed. Turn on Files in DocaDesk → Settings.",
            });
        }

        try
        {
            var result = Run(args);
            _audit?.Add("files." + Name["files_".Length..], Summarise(args), sessionId);
            return Task.FromResult(new McpToolResult { Text = FilesTools.Json(result) });
        }
        catch (Exception ex)
        {
            // The message is the whole diagnosis at the far end: DOCA shows it verbatim in the
            // Files tab. A bare type name there tells the person nothing they can act on.
            var why = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            _audit?.Add("files.fail", $"{Name}: {why}", sessionId);
            return Task.FromResult(new McpToolResult { IsError = true, Text = why });
        }
    }

    /// <summary>What the audit log records. Paths, because "which file" is the question asked of it later.</summary>
    protected virtual string Summarise(JsonNode? args) => Str(args, "path");

    protected static string Str(JsonNode? args, string key) => args?[key]?.GetValue<string>() ?? "";

    protected static JsonObject Schema(params string[] required)
    {
        var props = new JsonObject();
        foreach (var r in required) props[r] = new JsonObject { ["type"] = "string" };
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()) };
    }
}

file sealed class ListTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_list";
    public override string Description => "List a folder on this machine. An empty path is the user's home folder.";
    public override bool ReadOnlyHint => true;
    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
    };

    protected override object Run(JsonNode? args)
    {
        var dir = FilesTools.Resolve(Str(args, "path"));
        var entries = new List<object>();
        foreach (var full in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(full);
            try
            {
                var isDir = Directory.Exists(full);
                var info = isDir ? new DirectoryInfo(full) : (FileSystemInfo)new FileInfo(full);
                entries.Add(new
                {
                    name,
                    isDir,
                    size = isDir ? (long?)null : ((FileInfo)info).Length,
                    mtime = info.LastWriteTimeUtc.ToString("o"),
                });
            }
            catch
            {
                // Mirrors the host (modules/files.js:68): an entry that cannot be stat'd is still
                // listed, with nulls. Dropping it makes a locked file look deleted.
                entries.Add(new { name, isDir = false, size = (long?)null, mtime = (string?)null });
            }
        }
        return new { path = dir, entries };
    }
}

file sealed class ReadTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_read";
    public override string Description => "Read a file on this machine. encoding \"base64\" returns bytes.";
    public override bool ReadOnlyHint => true;
    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["encoding"] = new JsonObject { ["type"] = "string", ["description"] = "\"base64\" for bytes; text otherwise" },
        },
        ["required"] = new JsonArray("path"),
    };

    protected override object Run(JsonNode? args)
    {
        var path = FilesTools.Resolve(Str(args, "path"));
        var info = new FileInfo(path);
        var bytes = File.ReadAllBytes(path);
        var base64 = string.Equals(Str(args, "encoding"), "base64", StringComparison.OrdinalIgnoreCase);
        return new
        {
            content = base64 ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes),
            size = info.Length,
            mtime = info.LastWriteTimeUtc.ToString("o"),
        };
    }
}

file sealed class WriteTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_write";
    public override string Description => "Write a file on this machine, creating or replacing it. encoding \"base64\" for bytes.";
    public override bool ReadOnlyHint => false;
    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["content"] = new JsonObject { ["type"] = "string" },
            ["encoding"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("path", "content"),
    };

    protected override object Run(JsonNode? args)
    {
        var path = FilesTools.Resolve(Str(args, "path"));
        // An upload names its folder and lets the write create it (device-files.js:69 posts
        // "<dest>/<name>"), so a missing parent is the normal case, not an error.
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var content = Str(args, "content");
        if (string.Equals(Str(args, "encoding"), "base64", StringComparison.OrdinalIgnoreCase))
            File.WriteAllBytes(path, Convert.FromBase64String(content));
        else
            File.WriteAllText(path, content);
        return new { ok = true };
    }
}

file sealed class MkdirTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_mkdir";
    public override string Description => "Create a folder on this machine, including missing parents.";
    public override bool ReadOnlyHint => false;
    public override JsonObject InputSchema => Schema("path");

    protected override object Run(JsonNode? args)
    {
        Directory.CreateDirectory(FilesTools.Resolve(Str(args, "path")));
        return new { ok = true };
    }
}

file sealed class MoveTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_move";
    public override string Description => "Move or rename a file or folder on this machine.";
    public override bool ReadOnlyHint => false;
    public override JsonObject InputSchema => Schema("from", "to");

    protected override string Summarise(JsonNode? args) => $"{Str(args, "from")} -> {Str(args, "to")}";

    protected override object Run(JsonNode? args)
    {
        var from = FilesTools.Resolve(Str(args, "from"));
        var to = FilesTools.Resolve(Str(args, "to"));
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: true);
        return new { ok = true };
    }
}

file sealed class CopyTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_copy";
    public override string Description => "Copy a file or folder on this machine.";
    public override bool ReadOnlyHint => false;
    public override JsonObject InputSchema => Schema("from", "to");

    protected override string Summarise(JsonNode? args) => $"{Str(args, "from")} -> {Str(args, "to")}";

    protected override object Run(JsonNode? args)
    {
        var from = FilesTools.Resolve(Str(args, "from"));
        var to = FilesTools.Resolve(Str(args, "to"));
        if (Directory.Exists(from)) CopyTree(from, to);
        else File.Copy(from, to, overwrite: true);
        return new { ok = true };
    }

    // Paste of a folder in the Files tab is one files_copy (device-files.js:60), so this has to
    // recurse — File.Copy on a directory would only report "access denied" and confuse everyone.
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(from))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

file sealed class DeleteTool(FamilyConsent c, AuditLog? a) : FilesToolBase(c, a)
{
    public override string Name => "files_delete";
    public override string Description => "Delete files or folders on this machine.";
    public override bool ReadOnlyHint => false;
    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["paths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("paths"),
    };

    protected override string Summarise(JsonNode? args) =>
        string.Join(", ", (args?["paths"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? "") ?? []);

    protected override object Run(JsonNode? args)
    {
        var paths = (args?["paths"] as JsonArray)?.Select(p => p?.GetValue<string>()).Where(p => !string.IsNullOrWhiteSpace(p))
            ?? throw new ArgumentException("paths is required.");
        foreach (var p in paths)
        {
            var full = FilesTools.Resolve(p);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else File.Delete(full);
        }
        return new { ok = true };
    }
}
