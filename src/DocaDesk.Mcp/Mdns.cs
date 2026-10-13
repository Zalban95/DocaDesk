using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DocaDesk.Mcp;

/// <summary>
/// mDNS / DNS-SD as the hub's <c>modules/nearby/mdns.js</c> does it (DOCA PROTOCOL §22.5 <c>nearby_scan</c>): one PTR
/// query per service type sent to 224.0.0.251:5353 from a port of our own — a "legacy unicast" query (RFC 6762 §6.7)
/// that responders answer straight back to that port, so nothing has to share 5353 with Windows' own responder.
/// Answers and additional records (PTR, SRV, TXT, A) are read, with name compression, and grouped into instances.
/// Read-only: it asks who is there and listens; it never announces.
/// </summary>
public static class Mdns
{
    private const int Ptr = 12, Txt = 16, A = 1, Srv = 33;

    public sealed record Instance(string Name, string Type, string? Host, int Port, IReadOnlyList<string> Addresses, IReadOnlyDictionary<string, string> Txt);
    private sealed record Rec(string Name, int Type, object Data, string From);

    /// <summary>A query packet asking PTR for each name (id 0, no flags, as mDNS asks).</summary>
    public static byte[] BuildQuery(IEnumerable<string> names)
    {
        var list = names.ToList();
        using var m = new MemoryStream();
        m.Write([0, 0, 0, 0, (byte)(list.Count >> 8), (byte)list.Count, 0, 0, 0, 0, 0, 0]);
        foreach (var n in list)
        {
            foreach (var label in n.TrimEnd('.').Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                m.WriteByte((byte)b.Length);
                m.Write(b);
            }
            m.Write([0, 0, Ptr, 0, 1]);   // the root, PTR, class IN
        }
        return m.ToArray();
    }

    private static (string Name, int After) ReadName(byte[] buf, int off)
    {
        var labels = new List<string>();
        int? end = null;
        var jumps = 0;
        while (off < buf.Length)
        {
            int len = buf[off];
            if (len == 0) { off += 1; break; }
            if ((len & 0xc0) == 0xc0)
            {
                if (off + 1 >= buf.Length || ++jumps > 20) break;
                end ??= off + 2;
                off = ((len & 0x3f) << 8) | buf[off + 1];
                continue;
            }
            if (off + 1 + len > buf.Length) break;
            labels.Add(Encoding.UTF8.GetString(buf, off + 1, len));
            off += 1 + len;
        }
        return (string.Join('.', labels), end ?? off);
    }

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];

    private static List<Rec> Parse(byte[] buf, string from)
    {
        var out_ = new List<Rec>();
        if (buf.Length < 12) return out_;
        try
        {
            var qd = U16(buf, 4);
            var total = U16(buf, 6) + U16(buf, 8) + U16(buf, 10);
            var off = 12;
            for (var i = 0; i < qd; i++) off = ReadName(buf, off).After + 4;
            for (var i = 0; i < total && off + 10 <= buf.Length; i++)
            {
                var (name, at) = ReadName(buf, off);
                var type = U16(buf, at);
                var len = U16(buf, at + 8);
                var d = at + 10;
                if (d + len > buf.Length) break;
                object? data = type switch
                {
                    Ptr => ReadName(buf, d).Name,
                    Srv => (U16(buf, d + 4), ReadName(buf, d + 6).Name),
                    Txt => ReadTxt(buf, d, len),
                    A when len == 4 => $"{buf[d]}.{buf[d + 1]}.{buf[d + 2]}.{buf[d + 3]}",
                    _ => null,
                };
                if (data is not null) out_.Add(new Rec(name, type, data, from));
                off = d + len;
            }
        }
        catch (IndexOutOfRangeException) { /* a packet we cannot read: what was read stands */ }
        return out_;
    }

    private static Dictionary<string, string> ReadTxt(byte[] buf, int off, int len)
    {
        var o = new Dictionary<string, string>();
        var stop = off + len;
        while (off < stop)
        {
            int l = buf[off];
            var s = Encoding.UTF8.GetString(buf, off + 1, Math.Min(l, stop - off - 1));
            off += 1 + l;
            if (s.Length == 0) continue;
            var eq = s.IndexOf('=');
            if (eq < 0) o[s.ToLowerInvariant()] = "true"; else o[s[..eq].ToLowerInvariant()] = s[(eq + 1)..];
        }
        return o;
    }

    /// <summary>Packets (each with the address it came from) → instances, one per PTR target.</summary>
    public static List<Instance> Instances(IEnumerable<(byte[] Buf, string From)> packets)
    {
        var recs = packets.SelectMany(p => Parse(p.Buf, p.From)).ToList();
        IEnumerable<Rec> By(int type, string name) => recs.Where(r => r.Type == type && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        var seen = new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase);
        foreach (var ptr in recs.Where(r => r.Type == Ptr && !r.Name.StartsWith("_services._dns-sd._udp", StringComparison.OrdinalIgnoreCase)))
        {
            var inst = (string)ptr.Data;
            if (seen.ContainsKey(inst)) continue;
            var srv = By(Srv, inst).Select(r => ((int Port, string Target))r.Data).Cast<(int Port, string Target)?>().FirstOrDefault();
            var txt = new Dictionary<string, string>();
            foreach (var r in By(Txt, inst)) foreach (var (k, v) in (Dictionary<string, string>)r.Data) txt[k] = v;
            var host = srv?.Target;
            var addresses = (host is null ? [] : By(A, host).Select(r => (string)r.Data)).Append(ptr.From).Where(a => a.Length > 0).Distinct().ToList();
            var type = System.Text.RegularExpressions.Regex.Replace(ptr.Name, @"\.local\.?$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var name = inst.EndsWith(ptr.Name, StringComparison.OrdinalIgnoreCase) && inst.Length > ptr.Name.Length ? inst[..(inst.Length - ptr.Name.Length - 1)] : inst.Split('.')[0];
            seen[inst] = new Instance(name, type, host, srv?.Port ?? 0, addresses, txt);
        }
        return seen.Values.ToList();
    }

    /// <summary>Ask once (and again 400 ms later) from each address and listen for <paramref name="ms"/>.</summary>
    public static async Task<List<Instance>> BrowseAsync(IEnumerable<string> types, IReadOnlyList<IPAddress> from, int ms = 2500, int port = 5353, string group = "224.0.0.251")
    {
        var q = BuildQuery(types.Select(t => System.Text.RegularExpressions.Regex.Replace(t, @"\.local\.?$", "") + ".local"));
        var packets = new System.Collections.Concurrent.ConcurrentBag<(byte[], string)>();
        async Task One(IPAddress addr)
        {
            using var s = new UdpClient(new IPEndPoint(addr, 0));
            try { s.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); } catch { /* not every OS lets it be set */ }
            var to = new IPEndPoint(IPAddress.Parse(group), port);
            using var cts = new CancellationTokenSource(ms);
            await s.SendAsync(q, to).ConfigureAwait(false);
            _ = Task.Delay(400, cts.Token).ContinueWith(_ => { try { s.Send(q, q.Length, to); } catch { /* closed */ } }, TaskContinuationOptions.OnlyOnRanToCompletion);
            try
            {
                while (packets.Count < 2000)
                {
                    var r = await s.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    packets.Add((r.Buffer, r.RemoteEndPoint.Address.ToString()));
                }
            }
            catch (OperationCanceledException) { /* time is up */ }
            catch (SocketException) { /* this interface cannot multicast */ }
        }
        await Task.WhenAll((from.Count > 0 ? from : [IPAddress.Any]).Select(One)).ConfigureAwait(false);
        return Instances(packets);
    }
}
