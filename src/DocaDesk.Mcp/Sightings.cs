using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocaDesk.Mcp;

/// <summary>
/// What this machine saw, in the hub's own shape (its <c>modules/nearby/sightings.js</c>, ported for the sources a desk
/// reads — Cast, UPnP/DLNA and the neighbour table): the hub merges these unchanged with its own (PROTOCOL §22.5).
/// <c>keys</c> say who a thing is across sources; <c>kinds</c> are guesses with a weight; <c>can</c> what it does;
/// <c>how</c> the facts needed to do it.
/// </summary>
public static class Sightings
{
    private static readonly Regex Tailnet = new(@"^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.");

    private static JsonArray IpKey(string? ip) =>
        ip is not null && (NearbyNetwork.PrivateV4().IsMatch(ip) || Tailnet.IsMatch(ip)) ? [$"ip:{ip}"] : [];

    private static JsonObject Kind(string kind, int w) => new() { ["kind"] = kind, ["w"] = w };

    /// <summary>A Cast device from its mDNS announcement: TXT <c>fn</c> its name, <c>md</c> its model, <c>id</c> who it is.</summary>
    public static JsonObject Cast(Mdns.Instance x)
    {
        var model = x.Txt.GetValueOrDefault("md") ?? "";
        var name = x.Txt.GetValueOrDefault("fn") ?? x.Name;
        var ip = x.Addresses.FirstOrDefault();
        var speaker = Regex.IsMatch(model, "home|nest (mini|audio|hub)?|speaker|audio|soundbar", RegexOptions.IgnoreCase) && !Regex.IsMatch(model, "hub max|nest hub", RegexOptions.IgnoreCase);
        var keys = IpKey(ip);
        if (x.Txt.TryGetValue("id", out var id)) keys.Insert(0, $"cast:{id.ToLowerInvariant()}");
        var cast = new JsonObject { ["name"] = name, ["port"] = x.Port > 0 ? x.Port : 8009 };
        if (ip is not null) cast["ip"] = ip;
        return new JsonObject
        {
            ["source"] = "cast", ["name"] = name, ["keys"] = keys, ["addresses"] = ip is null ? new JsonArray() : new JsonArray(ip),
            ["maker"] = "Google", ["model"] = model, ["kinds"] = new JsonArray(Kind(speaker ? "speaker" : "tv", 9)), ["can"] = new JsonArray("cast"),
            ["how"] = new JsonObject { ["cast"] = cast },
        };
    }

    /// <summary>A UPnP / DLNA device from its description: a MediaRenderer can be played to (its AVTransport, its volume).</summary>
    public static JsonObject Upnp(Ssdp.Device d)
    {
        var words = $"{d.Manufacturer} {d.ModelName} {d.FriendlyName}";
        var services = d.Services ?? [];
        var avt = services.FirstOrDefault(s => s.Type.Contains("AVTransport", StringComparison.OrdinalIgnoreCase));
        var rc = services.FirstOrDefault(s => s.Type.Contains("RenderingControl", StringComparison.OrdinalIgnoreCase));
        var keys = IpKey(d.From.Length > 0 ? d.From : null);
        if (d.Udn.Length > 0) keys.Insert(0, $"udn:{d.Udn.ToLowerInvariant()}");
        var kinds = new JsonArray();
        if (d.DeviceType.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase))
        {
            var speaker = Regex.IsMatch(words, @"\b(sonos|speakers?|audio|soundbar|amplifier|receiver)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(words, @"\btv\b", RegexOptions.IgnoreCase);
            kinds.Add(Kind(speaker ? "speaker" : "tv", 7));
        }
        if (Regex.IsMatch(d.DeviceType, "InternetGatewayDevice|WANDevice|WFADevice", RegexOptions.IgnoreCase)) kinds.Add(Kind("router", 6));
        if (d.DeviceType.Contains("MediaServer", StringComparison.OrdinalIgnoreCase)) kinds.Add(Kind("storage", 3));
        if (d.DeviceType.Contains("Printer", StringComparison.OrdinalIgnoreCase)) kinds.Add(Kind("printer", 7));
        var can = new JsonArray();
        if (avt is not null) can.Add("dlna-play");
        if (d.PresentationUrl.Length > 0) can.Add("web");
        var how = new JsonObject();
        if (avt is not null)
        {
            var dlna = new JsonObject { ["control"] = avt.Control };
            if (rc is not null) dlna["volume"] = rc.Control;
            dlna["description"] = d.Location;
            dlna["ip"] = d.From.Length > 0 ? d.From : null;
            how["dlna"] = dlna;
        }
        var model = string.Join(' ', new[] { d.ModelName, d.ModelNumber }.Where(s => s.Length > 0));
        return new JsonObject
        {
            ["source"] = "ssdp", ["name"] = d.FriendlyName.Length > 0 ? d.FriendlyName : null, ["keys"] = keys,
            ["addresses"] = d.From.Length > 0 ? new JsonArray(d.From) : new JsonArray(),
            ["maker"] = d.Manufacturer.Length > 0 ? d.Manufacturer : null, ["model"] = model.Length > 0 ? model : null, ["kinds"] = kinds, ["can"] = can,
            ["web"] = d.PresentationUrl.StartsWith("http", StringComparison.Ordinal) ? d.PresentationUrl : null, ["how"] = how,
        };
    }

    /// <summary>A neighbour from the OS's table. The maker by its card is the hub's to read (it keeps the list).</summary>
    public static JsonObject Lan(NearbyNetwork.Neighbour n)
    {
        var keys = new JsonArray($"mac:{n.Mac}");
        foreach (var k in IpKey(n.Ip)) keys.Add(k!.DeepClone());
        return new JsonObject
        {
            ["source"] = "lan", ["name"] = null, ["keys"] = keys, ["addresses"] = new JsonArray(n.Ip), ["mac"] = n.Mac,
            // `private`: a card the device made up for this network (locally administered), as the hub marks it.
            ["maker"] = null, ["private"] = (Convert.ToInt32(n.Mac[..2], 16) & 2) != 0, ["kinds"] = n.Gateway ? new JsonArray(Kind("router", 8)) : new JsonArray(), ["can"] = new JsonArray(),
            ["detail"] = n.Gateway ? "the router (this network's gateway)" : null,
        };
    }
}
