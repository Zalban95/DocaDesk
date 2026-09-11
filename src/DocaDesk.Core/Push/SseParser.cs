using System.Text;

namespace DocaDesk.Core.Push;

/// <summary>Minimal SSE frame parser for event:/data:/id:/comment lines.</summary>
public sealed class SseParser
{
    private readonly StringBuilder _data = new();
    private string? _event;
    private string? _id;

    public sealed record Frame(string? Event, string? Id, string Data);

    public IEnumerable<Frame> PushLine(string? line)
    {
        if (line is null)
            yield break;

        if (line.Length == 0)
        {
            if (_data.Length > 0 || _event is not null)
            {
                var data = _data.ToString();
                if (data.EndsWith('\n'))
                    data = data[..^1];
                yield return new Frame(_event, _id, data);
            }
            _data.Clear();
            _event = null;
            // id persists per SSE spec until overwritten — we reset for simplicity between events
            _id = null;
            yield break;
        }

        if (line.StartsWith(':'))
        {
            // comment / heartbeat — signal via special event name
            yield return new Frame(":comment", null, line[1..].TrimStart());
            yield break;
        }

        var colon = line.IndexOf(':');
        string field, value;
        if (colon < 0)
        {
            field = line;
            value = "";
        }
        else
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
        }

        switch (field)
        {
            case "event":
                _event = value;
                break;
            case "data":
                _data.Append(value).Append('\n');
                break;
            case "id":
                _id = value;
                break;
            // retry ignored — we use capabilities backoff
        }
    }
}
