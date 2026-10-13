using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>
/// The <c>nearby</c> and <c>cast</c> families (DOCA PROTOCOL §22.5; cast-reach.md §6.2), the names and shapes
/// doca-client and DocaMobile lend: so the hub can look from this desk's network, and cast through it to a TV or
/// speaker there when the hub has no address on it. Portable .NET — the Linux client gets them unchanged.
///
///   nearby_network  which network this machine is on (subnet, gateway, a hash of the gateway's card)
///   nearby_scan     Cast devices, UPnP/DLNA players and the neighbour table, as the hub's own sightings
///   nearby_probe    whether an address answers on a port (a TCP connect; nothing is sent)
///   cast_serve      a door on this machine for one cast, answering the target alone (<see cref="CastDoor"/>)
///   cast_send       this machine's own Cast sender: show a page, play a sound, stop, volume (<see cref="CastSender"/>)
///   cast_end        close the door
/// </summary>
public static class NearbyCastTools
{
    public static readonly string[] Sources = ["cast", "mdns", "ssdp", "lan", "bluetooth"];

    /// <summary>Tests cast to 127.0.0.1 from 127.0.0.1, over a plain socket, as the hub's own tests do.</summary>
    internal static bool Loopback { get; set; }

    public static IReadOnlyList<FamilyTool> Create(FamilyConsent consent, CastDoor door, AuditLog? audit) =>
    [
        new FamilyTool(ToolFamilies.Nearby, new Tool("nearby_network", "Which network this machine is on: its subnet, the gateway, and a hash of the gateway's card (never the card itself).",
            true, [], (_, _) => NetworkAsync()), consent),
        new FamilyTool(ToolFamilies.Nearby, new Tool("nearby_scan", "What is around this machine: Cast speakers and TVs, UPnP/DLNA players and the network's neighbours, with the network it looked from.",
            true, [("sources", "array")], (a, _) => ScanAsync(a)), consent),
        new FamilyTool(ToolFamilies.Nearby, new Tool("nearby_probe", "Whether an address on this machine's network answers on a port (a TCP connect; nothing is sent).",
            true, [("address", "string"), ("port", "number")], (a, ct) => ProbeAsync(a, ct)), consent),
        new FamilyTool(ToolFamilies.Cast, new Tool("cast_serve", "Open a door on this machine for one cast to a TV or speaker on its network: a page forwarded from the hub, or a sound served from here, to that device only.",
            false, [("cast", "string"), ("target", "string"), ("page", "boolean"), ("media", "array"), ("ttlSec", "number")], (a, _) => Serve(door, a, audit)), consent),
        new FamilyTool(ToolFamilies.Cast, new Tool("cast_send", "This machine's own Cast sender: show a page, play a sound, stop, or set the volume of a Cast device on its network.",
            false, [("target", "object"), ("do", "string"), ("url", "string"), ("contentType", "string"), ("title", "string"), ("value", "number")], (a, _) => SendAsync(a, audit)), consent),
        new FamilyTool(ToolFamilies.Cast, new Tool("cast_end", "Close this machine's door for a cast.",
            false, [("cast", "string")], (a, _) => Task.FromResult<JsonNode>(new JsonObject { ["ok"] = door.End(Str(a, "cast")) })), consent),
    ];

    private static string Str(JsonNode? a, string k) => a?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    /// <summary>A number however it came: an int, a double, or a JSON element.</summary>
    private static double? Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDouble() : null
        : null;
    private static string Plain(string a) => a.StartsWith("::ffff:", StringComparison.Ordinal) ? a[7..] : a;
    private static bool Private(string ip) => NearbyNetwork.PrivateV4().IsMatch(ip) || (Loopback && ip == "127.0.0.1");

    private static async Task<JsonNode> NetworkAsync()
    {
        var own = NearbyNetwork.Interfaces().FirstOrDefault();
        return NearbyNetwork.Reading(own, await NearbyNetwork.NeighboursAsync(own?.Gateway).ConfigureAwait(false));
    }

    private static async Task<JsonNode> ScanAsync(JsonNode? a)
    {
        var asked = (a?["sources"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(x => x is not null && Sources.Contains(x)).Select(x => x!).ToList();
        var want = asked is { Count: > 0 } ? asked : [.. Sources];
        var ifs = NearbyNetwork.Interfaces();
        var own = ifs.FirstOrDefault();
        var neighbours = await NearbyNetwork.NeighboursAsync(own?.Gateway).ConfigureAwait(false);
        var from = ifs.Select(i => i.Address).ToList();
        var why = new JsonObject();
        async Task<IEnumerable<JsonObject>> Read(string id)
        {
            try
            {
                switch (id)
                {
                    case "cast":
                        return (await Mdns.BrowseAsync(["_googlecast._tcp"], from).ConfigureAwait(false))
                            .Where(x => x.Type.StartsWith("_googlecast", StringComparison.OrdinalIgnoreCase)).Select(Sightings.Cast);
                    case "ssdp":
                        return (await Ssdp.DiscoverAsync(own?.Address).ConfigureAwait(false)).Select(Sightings.Upnp);
                    case "lan":
                        return neighbours.Select(Sightings.Lan);
                    default:
                        why[id] = "this desk looks with Cast, UPnP and the neighbour table";
                        return [];
                }
            }
            catch (Exception ex) { why[id] = ex.Message; return []; }
        }
        var found = await Task.WhenAll(want.Select(Read)).ConfigureAwait(false);
        return new JsonObject
        {
            ["things"] = new JsonArray(found.SelectMany(x => x).ToArray<JsonNode?>()),
            ["sources"] = new JsonArray(want.Select(x => (JsonNode?)x).ToArray()),
            ["network"] = NearbyNetwork.Reading(own, neighbours),
            ["why"] = why,
            ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
    }

    private static async Task<JsonNode> ProbeAsync(JsonNode? a, CancellationToken ct)
    {
        var port = Num(a?["port"]) is > 0 and < 65536 and var n ? (int)n : 8009;
        var t0 = Environment.TickCount64;
        bool open;
        using (var tcp = new TcpClient())
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(3000);
            try { await tcp.ConnectAsync(Str(a, "address"), port, cts.Token).ConfigureAwait(false); open = true; }
            catch { open = false; }
        }
        return new JsonObject { ["open"] = open, ["ms"] = Environment.TickCount64 - t0 };
    }

    /// <summary>This machine's private IPv4 on the target's network (the same /24), or null.</summary>
    internal static IPAddress? AddressFor(string target)
    {
        if (Loopback && target == "127.0.0.1") return IPAddress.Loopback;
        var t = string.Join('.', target.Split('.').Take(3));
        return NearbyNetwork.Interfaces().Select(i => i.Address).FirstOrDefault(x => string.Join('.', x.ToString().Split('.').Take(3)) == t);
    }

    private static Task<JsonNode> Serve(CastDoor door, JsonNode? a, AuditLog? audit)
    {
        var id = Str(a, "cast");
        if (id.Length == 0) throw new ArgumentException("cast_serve needs the cast id.");
        if (id.Length > 80) id = id[..80];
        var target = Plain(Str(a, "target"));
        if (!Private(target)) throw new ArgumentException("The target is a private IPv4 address on this machine's network.");
        var here = AddressFor(target) ?? throw new ArgumentException($"This machine has no address on {target}'s network.");
        var media = new Dictionary<string, CastDoor.Media>();
        foreach (var f in (a?["media"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var token = Str(f, "token");
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-f]{32}$")) throw new ArgumentException("A media token is 32 hex characters.");
            media[token] = new CastDoor.Media(Convert.FromBase64String(Str(f, "data")), Str(f, "mime") is { Length: > 0 } m ? m : "application/octet-stream");
        }
        var page = a?["page"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
        var ttlSec = Num(a?["ttlSec"]) is > 0 and var t ? t : 3 * 3600;
        var @base = door.Serve(id, target, here, page, media, page ? TimeSpan.FromSeconds(ttlSec) : TimeSpan.FromMinutes(10));
        audit?.Add("cast.serve", $"{(page ? "page" : "sound")} to {target}");
        return Task.FromResult<JsonNode>(new JsonObject { ["base"] = @base });
    }

    private static async Task<JsonNode> SendAsync(JsonNode? a, AuditLog? audit)
    {
        var t = a?["target"];
        var host = Plain(t is JsonObject ? Str(t, "address") : Str(a, "target"));
        var port = Num(t?["port"]) is > 0 and < 65536 and var p ? (int)p : 8009;
        if (!Private(host)) throw new ArgumentException("The target is a private IPv4 address on this machine's network.");
        var what = Str(a, "do");
        await using var s = await CastSender.ConnectAsync(host, port, plain: Loopback).ConfigureAwait(false);
        audit?.Add("cast.send", $"{what} to {host}");
        switch (what)
        {
            case "page":
                {
                    var url = Str(a, "url");
                    if (!url.StartsWith("http://", StringComparison.Ordinal)) throw new ArgumentException("A page is an http:// address this machine serves (cast_serve's base).");
                    var app = await s.LaunchAsync(CastSender.Dashcast).ConfigureAwait(false);
                    await s.SendAsync(app.Transport, CastWire.NsDashcast, new JsonObject { ["url"] = url, ["force"] = true, ["reload"] = false, ["reload_time"] = 0 }).ConfigureAwait(false);
                    return new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["app"] = "page" } };
                }
            case "media":
                {
                    var app = await s.LaunchAsync(CastSender.Media).ConfigureAwait(false);
                    var title = Str(a, "title");
                    var r = await s.RequestAsync(app.Transport, CastWire.NsMedia, new JsonObject
                    {
                        ["type"] = "LOAD", ["autoplay"] = true,
                        ["media"] = new JsonObject
                        {
                            ["contentId"] = Str(a, "url"), ["contentType"] = Str(a, "contentType") is { Length: > 0 } ctype ? ctype : "audio/mpeg",
                            ["streamType"] = Str(a, "streamType") == "LIVE" ? "LIVE" : "BUFFERED",
                            ["metadata"] = new JsonObject { ["metadataType"] = 0, ["title"] = title.Length > 80 ? title[..80] : title },
                        },
                    }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    var rt = r.Data?["type"]?.GetValue<string>();
                    if (rt is "LOAD_FAILED" or "LOAD_CANCELLED") throw new InvalidOperationException($"It could not play it ({rt}).");
                    return new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["app"] = "media", ["playing"] = true } };
                }
            case "stop":
                {
                    var apps = (await s.StatusAsync().ConfigureAwait(false))["applications"] as JsonArray ?? [];
                    foreach (var x in apps.OfType<JsonObject>().Where(y => y["appId"]?.GetValue<string>() is CastSender.Dashcast or CastSender.Media).ToList())
                        await s.StopAsync(x["sessionId"]?.GetValue<string>()).ConfigureAwait(false);
                    return new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["app"] = null } };
                }
            case "volume":
                {
                    var v = Math.Clamp(Num(a?["value"]) ?? 0, 0, 1);
                    await s.VolumeAsync(v).ConfigureAwait(false);
                    return new JsonObject { ["ok"] = true, ["state"] = new JsonObject { ["volume"] = v } };
                }
            default:
                throw new ArgumentException("do is page, media, stop or volume.");
        }
    }
}

/// <summary>One family tool from a name, a sentence, its arguments and what it does; a thrown error is the refusal.</summary>
file sealed class Tool(string name, string description, bool readOnly, (string Name, string Type)[] input, Func<JsonNode?, CancellationToken, Task<JsonNode>> run) : IMcpTool
{
    public string Name => name;
    public string Description => description;
    public bool ReadOnlyHint => readOnly;
    public JsonObject InputSchema
    {
        get
        {
            var props = new JsonObject();
            foreach (var (n, t) in input) props[n] = new JsonObject { ["type"] = t };
            return new JsonObject { ["type"] = "object", ["properties"] = props };
        }
    }

    public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        try { return new McpToolResult { Text = (await run(args, ct).ConfigureAwait(false)).ToJsonString() }; }
        catch (Exception ex) { return new McpToolResult { IsError = true, Text = ex.Message }; }
    }
}
