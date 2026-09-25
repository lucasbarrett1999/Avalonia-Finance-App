using System.Text;
using System.Text.Json;

namespace Keel.Infrastructure.Portability;

/// <summary>
/// Reads a JSON document token by token from a stream with a small, growing buffer, so a bundle of any size
/// is imported without holding it in memory (<see cref="Utf8JsonReader"/> over refilled chunks, carrying its
/// state between them). Synchronous: the import runs on the thread pool.
/// </summary>
internal sealed class JsonTokenStream
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _final;
    private JsonReaderState _state = new(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });

    /// <summary>Reads from <paramref name="stream"/>.</summary>
    public JsonTokenStream(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>The current token.</summary>
    public JsonTokenType TokenType { get; private set; } = JsonTokenType.None;

    /// <summary>The current string, property name, or number text (null for other tokens).</summary>
    public string? Text { get; private set; }

    /// <summary>Moves to the next token; false at the end of the document.</summary>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public bool Read()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _final, _state);
            if (reader.Read())
            {
                TokenType = reader.TokenType;
                Text = reader.TokenType switch
                {
                    JsonTokenType.String or JsonTokenType.PropertyName => reader.GetString(),
                    JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                    _ => null,
                };
                _start += (int)reader.BytesConsumed;
                _state = reader.CurrentState;
                return true;
            }

            if (_final)
            {
                return false;
            }

            Fill();
        }
    }

    /// <summary>Reads the next token and requires <paramref name="type"/>.</summary>
    /// <exception cref="JsonException">Another token (or the end) came instead.</exception>
    public void Expect(JsonTokenType type)
    {
        if (!Read() || TokenType != type)
        {
            throw new JsonException($"Expected {type} but found {TokenType}.");
        }
    }

    /// <summary>Skips the value that starts at the current token (object and array contents included).</summary>
    public void SkipValue()
    {
        if (TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        var depth = 1;
        while (depth > 0 && Read())
        {
            depth += TokenType switch
            {
                JsonTokenType.StartObject or JsonTokenType.StartArray => 1,
                JsonTokenType.EndObject or JsonTokenType.EndArray => -1,
                _ => 0,
            };
        }
    }

    // Keeps the unread bytes, grows the buffer when one token fills it, and reads more of the stream.
    private void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _final = true;
        }
        else
        {
            _end += read;
        }
    }
}
