using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DocaDesk.Mcp;

/// <summary>
/// Which network this machine is on, and its neighbours, said the way the hub says it (<c>modules/nearby/network.js</c>,
/// <c>lan.js</c>; DOCA PROTOCOL §22.5 <c>nearby_network</c>): the first private IPv4 on a real interface with its
/// subnet, the gateway, and a short hash of the gateway's MAC — the router's card, the same whoever asks on that
/// network, and never the MAC itself. The neighbours are the OS's own table (<c>arp -a</c>, by argv, read-only).
/// </summary>
public static partial class NearbyNetwork
{
    [GeneratedRegex(@"^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)")] public static partial Regex PrivateV4();
    [GeneratedRegex(@"^(docker|br-|veth|virbr|vmnet|vbox|tailscale|utun|zt|wg|tun|tap|lo|vethernet|hyper-v)", RegexOptions.IgnoreCase)] private static partial Regex Virtual();

    public sealed record Own(string Iface, IPAddress Address, string Subnet, string Kind, string? Gateway);
    public sealed record Neighbour(string Ip, string Mac, bool Gateway);

    /// <summary>The interfaces facing a real network, each with its first private IPv4.</summary>
    public static List<Own> Interfaces()
    {
        var list = new List<Own>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (Virtual().IsMatch(n.Name) || Virtual().IsMatch(n.Description)) continue;
            var props = n.GetIPProperties();
            var a = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && PrivateV4().IsMatch(u.Address.ToString()));
            if (a is null) continue;
            var prefix = a.PrefixLength is > 0 and <= 32 ? a.PrefixLength : 24;
            var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
            var kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet";
            list.Add(new Own(n.Name, a.Address, $"{Network(a.Address, prefix)}/{prefix}", kind, gw?.ToString()));
        }
        // The one with a gateway first: that is the network this machine is on, the others are side links.
        return list.OrderBy(o => o.Gateway is null).ToList();
    }

    private static string Network(IPAddress ip, int prefix)
    {
        var b = ip.GetAddressBytes();
        var n = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        n &= mask;
        return $"{n >> 24 & 255}.{n >> 16 & 255}.{n >> 8 & 255}.{n & 255}";
    }

    /// <summary>
    /// <c>arp -a</c> → rows. Two shapes, as the hub reads them: Windows ("Interface: 192.168.1.5 --- 0x7", then
    /// "  192.168.1.1   d4-35-…   dynamic", in the OS's language) and BSD/net-tools ("? (192.168.1.1) at d4:35:… on en0"). MACs lower case,
    /// colon-separated, so the gateway's hash is the same whoever reads it; multicast and broadcast cards dropped.
    /// </summary>
    public static List<(string Ip, string Mac)> ParseArp(string text)
    {
        var rows = new List<(string, string)>();
        foreach (var line in Regex.Split(text ?? "", @"\r?\n"))
        {
            var bsd = Regex.Match(line, @"\((\d{1,3}(?:\.\d{1,3}){3})\)\s+at\s+([0-9a-f:]+)", RegexOptions.IgnoreCase);
            if (bsd.Success) { rows.Add((bsd.Groups[1].Value, Normalize(bsd.Groups[2].Value))); continue; }
            var win = Regex.Match(line, @"^\s+(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-f]{2}(?:-[0-9a-f]{2}){5})\s+(\w+)", RegexOptions.IgnoreCase);
            // Not by the word "dynamic": Windows writes it in its own language. The static rows are the multicast and
            // broadcast cards, which the filter below drops by their address.
            if (win.Success) rows.Add((win.Groups[1].Value, Normalize(win.Groups[2].Value)));
        }
        return rows.Where(r => r.Item2.Length == 17 && (Convert.ToInt32(r.Item2[..2], 16) & 1) == 0 && r.Item2 != "ff:ff:ff:ff:ff:ff").ToList();
    }

    public static string Normalize(string mac)
    {
        var parts = mac.ToLowerInvariant().Split(':', '-');
        return parts.Length == 6 ? string.Join(':', parts.Select(p => p.PadLeft(2, '0'))) : "";
    }

    public static string? HashMac(string? mac) =>
        string.IsNullOrEmpty(mac) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mac.ToLowerInvariant()))).ToLowerInvariant()[..12];

    /// <summary>The OS's neighbour table, one row per address, the gateway marked.</summary>
    public static async Task<List<Neighbour>> NeighboursAsync(string? gateway)
    {
        string text;
        try
        {
            var psi = new ProcessStartInfo("arp", OperatingSystem.IsWindows() ? "-a" : "-an") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            text = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        }
        catch { return []; }
        return ParseArp(text).DistinctBy(r => r.Ip).Take(254).Select(r => new Neighbour(r.Ip, r.Mac, r.Ip == gateway)).ToList();
    }

    /// <summary>{subnet, address, gateway, gatewayMacHash, kind, ssid, at} — as the hub's network.js says it.</summary>
    public static JsonObject Reading(Own? own, IEnumerable<Neighbour> neighbours) => new()
    {
        ["subnet"] = own?.Subnet,
        ["address"] = own?.Address.ToString(),
        ["gateway"] = own?.Gateway,
        ["gatewayMacHash"] = HashMac(neighbours.FirstOrDefault(n => n.Gateway)?.Mac),
        ["kind"] = own?.Kind,
        ["ssid"] = null,
        ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
    };
}
