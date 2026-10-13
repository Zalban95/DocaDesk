using System.Text;
using System.Text.Json.Nodes;

namespace DocaDesk.Mcp;

/// <summary>
/// Google Cast v2's wire, with no protobuf library — the hub's own <c>modules/cast/castv2/wire.js</c>, ported byte for
/// byte as DocaMobile's <c>CastWire.kt</c> is (DOCA PROTOCOL §22.5): each message is a 4-byte big-endian length, then a
/// protobuf <c>CastMessage</c> — 1 protocol_version (varint, 0), 2 source_id, 3 destination_id, 4 namespace (strings),
/// 5 payload_type (varint, 0 = string), 6 payload_utf8 (a JSON string). Only varints and length-delimited fields.
/// </summary>
public static class CastWire
{
    public const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    public const string NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string NsReceiver = "urn:x-cast:com.google.cast.receiver";
    public const string NsMedia = "urn:x-cast:com.google.cast.media";
    public const string NsDashcast = "urn:x-cast:com.madmod.dashcast";

    public sealed record Message(string Source, string Destination, string Namespace, JsonObject? Data);

    private static void Varint(MemoryStream m, ulong n)
    {
        while (n > 127) { m.WriteByte((byte)((n & 127) | 128)); n >>= 7; }
        m.WriteByte((byte)n);
    }

    private static void Field(MemoryStream m, int n, string s)
    {
        var v = Encoding.UTF8.GetBytes(s);
        Varint(m, (ulong)((n << 3) | 2));
        Varint(m, (ulong)v.Length);
        m.Write(v);
    }

    private static void Num(MemoryStream m, int n, ulong v) { Varint(m, (ulong)(n << 3)); Varint(m, v); }

    /// <summary>One framed message from <paramref name="source"/> to <paramref name="destination"/>, its payload JSON.</summary>
    public static byte[] Encode(string source, string destination, string ns, JsonNode payload)
    {
        using var body = new MemoryStream();
        Num(body, 1, 0); Field(body, 2, source); Field(body, 3, destination); Field(body, 4, ns); Num(body, 5, 0);
        Field(body, 6, payload.ToJsonString());
        var n = (int)body.Length;
        var framed = new byte[4 + n];
        framed[0] = (byte)(n >> 24); framed[1] = (byte)(n >> 16); framed[2] = (byte)(n >> 8); framed[3] = (byte)n;
        body.GetBuffer().AsSpan(0, n).CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>One message's body (without its length). <c>Data</c> is null when the payload is not a JSON object.</summary>
    public static Message Decode(ReadOnlySpan<byte> buf)
    {
        var fields = new Dictionary<int, byte[]>();
        var i = 0;
        ulong Read(ReadOnlySpan<byte> b)
        {
            ulong r = 0; var s = 0; byte x;
            do { x = b[i++]; r += (ulong)(x & 127) << s; s += 7; } while ((x & 128) != 0 && i < b.Length);
            return r;
        }
        while (i < buf.Length)
        {
            var key = Read(buf); var n = (int)(key >> 3); var type = (int)(key & 7);
            if (type == 0) { Read(buf); continue; }
            if (type != 2) break;   // nothing else is used
            var l = (int)Read(buf);
            if (l < 0 || i + l > buf.Length) break;
            fields[n] = buf.Slice(i, l).ToArray();
            i += l;
        }
        string Str(int n) => fields.TryGetValue(n, out var v) ? Encoding.UTF8.GetString(v) : "";
        JsonObject? data = null;
        try { data = JsonNode.Parse(Str(6)) as JsonObject; } catch { /* not JSON */ }
        return new Message(Str(2), Str(3), Str(4), data);
    }

    /// <summary>Takes bytes as they arrive; calls back per whole message. Never holds more than a megabyte waiting.</summary>
    public sealed class Reader(Action<Message> onMessage)
    {
        private byte[] _buf = [];

        public void Feed(ReadOnlySpan<byte> chunk)
        {
            var b = new byte[_buf.Length + chunk.Length];
            _buf.CopyTo(b, 0);
            chunk.CopyTo(b.AsSpan(_buf.Length));
            _buf = b;
            while (_buf.Length >= 4)
            {
                var n = (_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3];
                if (n < 0 || n > (1 << 20)) { _buf = []; return; }
                if (_buf.Length < 4 + n) return;
                var m = Decode(_buf.AsSpan(4, n));
                _buf = _buf.AsSpan(4 + n).ToArray();
                onMessage(m);
            }
        }
    }
}
