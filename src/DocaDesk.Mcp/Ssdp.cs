using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DocaDesk.Mcp;

/// <summary>
/// UPnP / DLNA as the hub's <c>modules/nearby/ssdp.js</c> (ported as DocaMobile's <c>Ssdp.kt</c> is; DOCA PROTOCOL §22.5
/// <c>nearby_scan</c>): one M-SEARCH (ssdp:all) to 239.255.255.250:1900, the answers' LOCATION read, and each
/// description fetched with a short timeout — only from a private address and only over http, so a reply naming
/// somebody else's server is never followed.
/// </summary>
public static partial class Ssdp
{
    public static readonly string Search = string.Join("\r\n", "M-SEARCH * HTTP/1.1", "HOST: 239.255.255.250:1900", "MAN: \"ssdp:discover\"", "MX: 2", "ST: ssdp:all", "", "");

    [GeneratedRegex(@"^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|169\.254\.|127\.)")] private static partial Regex Private();

    public sealed record Service(string Type, string Control);
    public sealed record Device(string Location, string From, string FriendlyName = "", string Manufacturer = "", string ModelName = "", string ModelNumber = "",
        string DeviceType = "", string Udn = "", string PresentationUrl = "", IReadOnlyList<Service>? Services = null);

    /// <summary>One answer → its headers, names in lower case; null when it is not HTTP-shaped or has no LOCATION.</summary>
    public static Dictionary<string, string>? ParseResponse(string text)
    {
        var lines = Regex.Split(text, @"\r?\n");
        if (!Regex.IsMatch(lines.FirstOrDefault() ?? "", @"^(HTTP/1\.[01] 200|NOTIFY \* HTTP)", RegexOptions.IgnoreCase)) return null;
        var h = new Dictionary<string, string>();
        foreach (var l in lines.Skip(1)) { var i = l.IndexOf(':'); if (i > 0) h[l[..i].Trim().ToLowerInvariant()] = l[(i + 1)..].Trim(); }
        return h.ContainsKey("location") ? h : null;
    }

    public static bool Fetchable(string loc) => Uri.TryCreate(loc, UriKind.Absolute, out var u) && u.Scheme == "http" && Private().IsMatch(u.Host);

    private static string Unxml(string s) => s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&").Trim();
    private static string Tag(string xml, string name)
    {
        var m = Regex.Match(xml, $@"<(?:\w+:)?{name}>([\s\S]*?)</(?:\w+:)?{name}>", RegexOptions.IgnoreCase);
        return m.Success ? Unxml(m.Groups[1].Value) : "";
    }

    /// <summary>A description → its root device; relative URLs resolved against the description's address.</summary>
    public static Device ParseDescription(string xml, string location, string from)
    {
        var dev = Regex.Match(xml, @"<device>([\s\S]*)</device>", RegexOptions.IgnoreCase).Groups[1].Value;
        var top = Regex.Replace(Regex.Replace(dev, @"<deviceList>[\s\S]*</deviceList>", "", RegexOptions.IgnoreCase), @"<serviceList>[\s\S]*?</serviceList>", "", RegexOptions.IgnoreCase);
        var urlBase = Tag(xml, "URLBase") is { Length: > 0 } b ? b : location;
        string Abs(string u) => u.Length == 0 || !Uri.TryCreate(urlBase, UriKind.Absolute, out var bu) || !Uri.TryCreate(bu, u, out var r) ? "" : r.ToString();
        var services = Regex.Matches(dev, @"<service>([\s\S]*?)</service>", RegexOptions.IgnoreCase)
            .Select(m => new Service(Tag(m.Groups[1].Value, "serviceType"), Abs(Tag(m.Groups[1].Value, "controlURL")))).Where(s => s.Type.Length > 0).Take(30).ToList();
        return new Device(location, from, Tag(top, "friendlyName"), Tag(top, "manufacturer"), Tag(top, "modelName"), Tag(top, "modelNumber"),
            Tag(top, "deviceType"), Tag(top, "UDN"), Abs(Tag(top, "presentationURL")), services);
    }

    /// <summary>Ask and gather the answers for <paramref name="ms"/>: LOCATION → the sender's address.</summary>
    public static async Task<Dictionary<string, string>> SearchAsync(IPAddress? local, int ms = 3000, IPEndPoint? to = null)
    {
        var found = new Dictionary<string, string>();
        using var s = new UdpClient(new IPEndPoint(local ?? IPAddress.Any, 0));
        try { s.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4); } catch { /* not every OS lets it be set */ }
        var data = Encoding.ASCII.GetBytes(Search);
        to ??= new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        await s.SendAsync(data, to).ConfigureAwait(false);
        using var cts = new CancellationTokenSource(ms);
        _ = Task.Delay(500, cts.Token).ContinueWith(_ => { try { s.Send(data, data.Length, to); } catch { /* closed */ } }, TaskContinuationOptions.OnlyOnRanToCompletion);
        try
        {
            while (found.Count < 300)
            {
                var r = await s.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var h = ParseResponse(Encoding.UTF8.GetString(r.Buffer));
                if (h is not null) found.TryAdd(h["location"], r.RemoteEndPoint.Address.ToString());
            }
        }
        catch (OperationCanceledException) { /* time is up */ }
        catch (SocketException) { /* this interface cannot multicast */ }
        return found;
    }

    /// <summary>One sweep: the answers, each with its description read (at most 40).</summary>
    public static async Task<List<Device>> DiscoverAsync(IPAddress? local, int ms = 3000)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var found = await SearchAsync(local, ms).ConfigureAwait(false);
        var reads = found.Where(kv => Fetchable(kv.Key)).Take(40).Select(async kv =>
        {
            string xml;
            try
            {
                using var res = await http.GetAsync(kv.Key).ConfigureAwait(false);
                xml = res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
            }
            catch { xml = ""; }
            if (xml.Length > 256 * 1024) xml = xml[..(256 * 1024)];
            return xml.Length == 0 ? new Device(kv.Key, kv.Value) : ParseDescription(xml, kv.Key, kv.Value);
        });
        return (await Task.WhenAll(reads).ConfigureAwait(false)).ToList();
    }
}
