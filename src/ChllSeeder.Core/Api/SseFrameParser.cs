using System.Text;

namespace ChllSeeder.Core.Api;

/// <summary>A dispatched Server-Sent-Event: its <c>event:</c> type and joined <c>data:</c> payload.</summary>
public readonly record struct SseFrame(string EventType, string Data);

/// <summary>
/// A minimal, allocation-light Server-Sent-Events frame parser (the subset the
/// CHLL stats stream uses): <c>event:</c> / <c>data:</c> fields, multi-line
/// <c>data:</c> accumulation, comment/keepalive lines (leading <c>:</c>), and
/// blank-line dispatch. Default event type is <c>message</c>. Stateful: feed it
/// one line at a time as they arrive off the wire. Pure (no I/O) so it unit-tests
/// directly. Replaces the <c>reqwest_eventsource</c> dependency used by the Rust client.
/// </summary>
public sealed class SseFrameParser
{
    private const string DefaultEvent = "message";

    private string _eventType = DefaultEvent;
    private readonly StringBuilder _data = new();
    private bool _hasField;

    /// <summary>Feed one line (without its trailing newline). Returns a frame when the line is the
    /// blank-line terminator of a non-empty event; otherwise null.</summary>
    public SseFrame? Feed(string line)
    {
        // Blank line → dispatch the buffered event (if any) and reset.
        if (line.Length == 0)
        {
            if (!_hasField)
            {
                return null;
            }
            var frame = new SseFrame(_eventType, _data.ToString());
            Reset();
            return frame;
        }

        // Comment / keepalive line.
        if (line[0] == ':')
        {
            return null;
        }

        var colon = line.IndexOf(':');
        string field, value;
        if (colon < 0)
        {
            // A line with no colon is a field name with an empty value.
            field = line;
            value = "";
        }
        else
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            // A single leading space after the colon is stripped.
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }
        }

        switch (field)
        {
            case "event":
                _eventType = value;
                _hasField = true;
                break;
            case "data":
                if (_data.Length > 0)
                {
                    _data.Append('\n');
                }
                _data.Append(value);
                _hasField = true;
                break;
            // id / retry and unknown fields are accepted but unused here.
            default:
                _hasField = true;
                break;
        }

        return null;
    }

    private void Reset()
    {
        _eventType = DefaultEvent;
        _data.Clear();
        _hasField = false;
    }

    /// <summary>Parse a complete SSE text block into all its frames. Convenience for tests.</summary>
    public static IEnumerable<SseFrame> ParseAll(string text)
    {
        var parser = new SseFrameParser();
        // Normalize CRLF → LF, then split on LF preserving blank lines.
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (parser.Feed(line) is { } frame)
            {
                yield return frame;
            }
        }
    }
}
