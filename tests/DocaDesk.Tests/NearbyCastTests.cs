using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>
/// The `nearby` and `cast` families (DOCA PROTOCOL §22.5): the shapes the hub merges unchanged, the Cast wire byte for
/// byte with the hub's own, and a cast end to end on this machine — a door that answers its target alone, serves a
/// sound, forwards a page to a stand-in hub adding nothing of ours, and a sender that drives a stand-in Cast device.
/// </summary>
[Collection("cast-loopback")]
public class NearbyCastTests
{
    /* ── The wire, as the hub writes it ───────────────────── */

    [Fact]
    public void A_cast_message_is_byte_for_byte_the_hubs()
    {
        // modules/cast/castv2/wire.js encode('sender-doca', 'receiver-0', NS connection, {type: 'CONNECT'})
        const string hub = "0000005b0800120b73656e6465722d646f63611a0a72656365697665722d30222875726e3a782d636173743a636f6d2e676f6f676c652e636173742e74702e636f6e6e656374696f6e280032127b2274797065223a22434f4e4e454354227d";
        var ours = CastWire.Encode("sender-doca", "receiver-0", CastWire.NsConnection, new JsonObject { ["type"] = "CONNECT" });
        Assert.Equal(hub, Convert.ToHexString(ours).ToLowerInvariant());

        CastWire.Message? got = null;
        new CastWire.Reader(m => got = m).Feed(ours);
        Assert.Equal("receiver-0", got!.Destination);
        Assert.Equal("CONNECT", got.Data?["type"]?.GetValue<string>());
    }

    /* ── What is around: the hub's shapes ─────────────────── */

    [Fact]
    public void The_gateway_hash_is_the_hubs_so_two_lookers_on_one_network_agree()
    {
        // modules/nearby/network.js hashMac('d4:35:1d:0a:0b:0c')
        Assert.Equal("7fe147b499c0", NearbyNetwork.HashMac("d4:35:1d:0a:0b:0c"));
        Assert.Equal("7fe147b499c0", NearbyNetwork.HashMac(NearbyNetwork.Normalize("D4-35-1D-0A-0B-0C")));
    }

    [Fact]
    public void The_neighbour_table_is_read_in_any_language_and_group_cards_are_dropped()
    {
        const string windows = "\r\nInterfaccia: 192.168.1.5 --- 0x7\r\n  Indirizzo Internet    Indirizzo fisico      Tipo\r\n" +
                               "  192.168.1.1           d4-35-1d-0a-0b-0c     dinamico\r\n  192.168.1.255         ff-ff-ff-ff-ff-ff     statico\r\n" +
                               "  224.0.0.251           01-00-5e-00-00-fb     statico\r\n";
        const string bsd = "? (10.0.0.9) at 3c:22:fb:1:2:3 on en0 ifscope [ethernet]\n";
        Assert.Equal(new[] { ("192.168.1.1", "d4:35:1d:0a:0b:0c") }, NearbyNetwork.ParseArp(windows));
        Assert.Equal(new[] { ("10.0.0.9", "3c:22:fb:01:02:03") }, NearbyNetwork.ParseArp(bsd));
    }

    [Fact]
    public void A_cast_device_from_mdns_is_the_hubs_sighting()
    {
        var packet = MdnsAnswer("_googlecast._tcp.local", "Chromecast-abc._googlecast._tcp.local", "abc.local", 8009, "192.168.1.40",
            ["id=ABC123", "fn=Living Room TV", "md=Chromecast"]);
        var inst = Assert.Single(Mdns.Instances([(packet, "192.168.1.40")]));
        var s = Sightings.Cast(inst);
        Assert.Equal("cast", s["source"]!.GetValue<string>());
        Assert.Equal("Living Room TV", s["name"]!.GetValue<string>());
        Assert.Equal(new[] { "cast:abc123", "ip:192.168.1.40" }, s["keys"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal("tv", s["kinds"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(8009, s["how"]!["cast"]!["port"]!.GetValue<int>());
    }

    [Fact]
    public void A_dlna_renderer_is_playable_with_its_volume_and_a_stranger_location_is_not_followed()
    {
        Assert.NotNull(Ssdp.ParseResponse("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.50:1400/xml/device_description.xml\r\nST: ssdp:all\r\n\r\n"));
        Assert.Null(Ssdp.ParseResponse("HTTP/1.1 200 OK\r\nST: ssdp:all\r\n\r\n"));
        Assert.False(Ssdp.Fetchable("http://example.com/desc.xml"));
        Assert.False(Ssdp.Fetchable("https://192.168.1.50/desc.xml"));
        const string xml = "<root><device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType><friendlyName>Kitchen</friendlyName>" +
                           "<manufacturer>Sonos</manufacturer><modelName>One</modelName><UDN>uuid:RINCON_1</UDN><serviceList>" +
                           "<service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/MediaRenderer/AVTransport/Control</controlURL></service>" +
                           "<service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>/MediaRenderer/RenderingControl/Control</controlURL></service>" +
                           "</serviceList></device></root>";
        var s = Sightings.Upnp(Ssdp.ParseDescription(xml, "http://192.168.1.50:1400/xml/device_description.xml", "192.168.1.50"));
        Assert.Equal("speaker", s["kinds"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("dlna-play", s["can"]![0]!.GetValue<string>());
        Assert.Equal("http://192.168.1.50:1400/MediaRenderer/AVTransport/Control", s["how"]!["dlna"]!["control"]!.GetValue<string>());
        Assert.Equal("http://192.168.1.50:1400/MediaRenderer/RenderingControl/Control", s["how"]!["dlna"]!["volume"]!.GetValue<string>());
        Assert.Equal("udn:uuid:rincon_1", s["keys"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_family_list_is_the_hubs_when_the_hub_is_checked_out_beside_it()
    {
        if (DocaRepo.ClientJs is null) return;   // no checkout: the count test below still holds the shape
        var file = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(DocaRepo.ClientJs)!)!, "devices-control.js");
        var m = Regex.Match(File.ReadAllText(file), @"const FAMILIES = \[([^\]]*)\]");
        Assert.True(m.Success, "devices-control.js no longer says FAMILIES as this test reads it");
        var hub = Regex.Matches(m.Groups[1].Value, "'([a-z]+)'").Select(x => x.Groups[1].Value).ToList();
        if (!hub.Contains(ToolFamilies.Cast)) return;   // a checkout from before §22.5: it says nothing about this list
        Assert.Equal(hub, ToolFamilies.All);
    }

    /* ── A cast on this machine ───────────────────────────── */

    [Fact]
    public async Task A_sound_is_served_from_here_to_its_target_alone()
    {
        using var door = new CastDoor(() => null, () => null);
        var token = new string('a', 32);
        var b = door.Serve("c1", "127.0.0.1", IPAddress.Loopback, false, new Dictionary<string, CastDoor.Media> { [token] = new(Encoding.UTF8.GetBytes("RIFF"), "audio/wav") }, TimeSpan.FromMinutes(1));
        using var http = new HttpClient();
        var r = await http.GetAsync($"{b}/api/cast/media/{token}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("audio/wav", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("RIFF", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{b}/api/cast/media/{new string('b', 32)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{b}/")).StatusCode);   // a sound-only cast forwards nothing

        using var other = new CastDoor(() => null, () => null);   // a door for another address answers nobody here
        var o = other.Serve("c2", "127.0.0.2", IPAddress.Loopback, false, new Dictionary<string, CastDoor.Media> { [token] = new([1], "audio/wav") }, TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{o}/api/cast/media/{token}")).StatusCode);
        Assert.True(door.End("c1"));
        Assert.False(door.End("c1"));
    }

    [Fact]
    public async Task A_page_is_forwarded_to_the_hub_with_nothing_of_ours_and_its_cookie_made_plain()
    {
        // A stand-in hub: answers once, keeping what it was sent.
        var hub = new TcpListener(IPAddress.Loopback, 0);
        hub.Start();
        var hubBase = new Uri($"http://127.0.0.1:{((IPEndPoint)hub.LocalEndpoint).Port}");
        var seen = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using var c = await hub.AcceptTcpClientAsync();
            var s = c.GetStream();
            var sb = new StringBuilder();
            var buf = new byte[4096];
            while (!sb.ToString().Contains("\r\n\r\n")) sb.Append(Encoding.ASCII.GetString(buf, 0, await s.ReadAsync(buf)));
            seen.SetResult(sb.ToString());
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nSet-Cookie: v=1; Path=/; Secure; HttpOnly\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"));
        });

        using var door = new CastDoor(() => new HttpMessageInvoker(new SocketsHttpHandler { UseCookies = false }), () => hubBase);
        var b = door.Serve("p1", "127.0.0.1", IPAddress.Loopback, true, new Dictionary<string, CastDoor.Media>(), TimeSpan.FromMinutes(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{b}/?view=ambient");
        req.Headers.TryAddWithoutValidation("Origin", b);
        req.Headers.TryAddWithoutValidation("X-Doca-Password", "nope");
        req.Headers.TryAddWithoutValidation("X-Cast-View", "vs123");
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false });
        var r = await http.SendAsync(req);
        Assert.Equal("hello", await r.Content.ReadAsStringAsync());
        Assert.DoesNotContain("secure", string.Join(";", r.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        var sent = await seen.Task;
        Assert.StartsWith("GET /?view=ambient HTTP/1.1", sent);
        Assert.Contains($"Host: {hubBase.Authority}", sent);
        Assert.Contains($"Origin: {hubBase.GetLeftPart(UriPartial.Authority)}", sent);
        Assert.Contains("X-Cast-View: vs123", sent);                                   // the TV's own view session goes on
        Assert.DoesNotContain("X-Doca-Password", sent, StringComparison.OrdinalIgnoreCase);   // nothing that is DOCA's own
        Assert.DoesNotContain("Authorization", sent, StringComparison.OrdinalIgnoreCase);
        hub.Stop();
    }

    [Fact]
    public async Task The_sender_shows_a_page_on_a_cast_device()
    {
        var dev = new TcpListener(IPAddress.Loopback, 0);
        dev.Start();
        var port = ((IPEndPoint)dev.LocalEndpoint).Port;
        var dash = new TaskCompletionSource<JsonObject>();
        _ = Task.Run(async () =>
        {
            using var c = await dev.AcceptTcpClientAsync();
            var s = c.GetStream();
            var reader = new CastWire.Reader(m =>
            {
                var type = m.Data?["type"]?.GetValue<string>();
                if (type == "LAUNCH")
                {
                    var status = new JsonObject { ["type"] = "RECEIVER_STATUS", ["requestId"] = m.Data!["requestId"]!.GetValue<int>(),
                        ["status"] = new JsonObject { ["applications"] = new JsonArray(new JsonObject { ["appId"] = CastSender.Dashcast, ["sessionId"] = "s1", ["transportId"] = "web-1" }) } };
                    s.Write(CastWire.Encode("receiver-0", m.Source, CastWire.NsReceiver, status));
                }
                if (m.Namespace == CastWire.NsDashcast) dash.TrySetResult(m.Data!);
            });
            var buf = new byte[4096];
            int n;
            try { while ((n = await s.ReadAsync(buf)) > 0) reader.Feed(buf.AsSpan(0, n)); } catch { /* closed */ }
        });

        NearbyCastTools.Loopback = true;
        try
        {
            var send = NearbyCastTools.Create(FamilyConsent.InMemory(), new CastDoor(() => null, () => null), null).Single(t => t.Name == "cast_send");
            var consent = FamilyConsent.InMemory();
            consent.SetGranted(ToolFamilies.Cast, true);
            var tool = NearbyCastTools.Create(consent, new CastDoor(() => null, () => null), null).Single(t => t.Name == "cast_send");
            var r = await tool.CallAsync(new JsonObject { ["target"] = new JsonObject { ["address"] = "127.0.0.1", ["port"] = port }, ["do"] = "page", ["url"] = "http://127.0.0.1:1/" }, "t", default);
            Assert.False(r.IsError, r.Text);
            var d = await dash.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("http://127.0.0.1:1/", d["url"]!.GetValue<string>());

            var refused = await send.CallAsync(new JsonObject { ["do"] = "stop" }, "t", default);   // not allowed on this machine
            Assert.True(refused.IsError);
            var outside = await tool.CallAsync(new JsonObject { ["target"] = new JsonObject { ["address"] = "8.8.8.8" }, ["do"] = "stop" }, "t", default);
            Assert.True(outside.IsError);
        }
        finally { NearbyCastTools.Loopback = false; dev.Stop(); }
    }

    /// <summary>One mDNS answer: PTR type → instance, SRV instance → host:port, TXT, A host → address. No compression.</summary>
    private static byte[] MdnsAnswer(string type, string instance, string host, int port, string ip, string[] txt)
    {
        var m = new MemoryStream();
        void Name(string n) { foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); m.WriteByte((byte)b.Length); m.Write(b); } m.WriteByte(0); }
        void U16(int v) { m.WriteByte((byte)(v >> 8)); m.WriteByte((byte)v); }
        void Rec(string n, int t, byte[] data) { Name(n); U16(t); U16(1); m.Write([0, 0, 0, 120]); U16(data.Length); m.Write(data); }
        byte[] Bytes(Action<MemoryStream> w) { var x = new MemoryStream(); var keep = m; m = x; w(x); m = keep; return x.ToArray(); }
        m.Write([0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0]);
        Rec(type, 12, Bytes(_ => Name(instance)));
        Rec(instance, 33, Bytes(_ => { U16(0); U16(0); U16(port); Name(host); }));
        Rec(instance, 16, Bytes(x => { foreach (var t in txt) { var b = Encoding.UTF8.GetBytes(t); x.WriteByte((byte)b.Length); x.Write(b); } }));
        Rec(host, 1, ip.Split('.').Select(byte.Parse).ToArray());
        return m.ToArray();
    }
}

[CollectionDefinition("cast-loopback", DisableParallelization = true)]
public class CastLoopbackCollection;
