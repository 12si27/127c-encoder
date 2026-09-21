namespace Encoder127c.Diagnostics;

internal sealed class BoundedLogBuffer : IProgress<string>
{
    private const int MaxLines = 2_000;
    private const int MaxCharacters = 250_000;
    private const int TrimLines = 200;
    private readonly object _sync = new();
    private readonly Queue<string> _lines = new();
    private int _characters;
    private bool _changed;

    public void Report(string message)
    {
        lock (_sync)
        {
            using var reader = new StringReader(message);
            while (reader.ReadLine() is { } line)
            {
                var maxLineLength = MaxCharacters - Environment.NewLine.Length;
                if (line.Length > maxLineLength)
                {
                    line = line[^maxLineLength..];
                }

                _lines.Enqueue(line);
                _characters += line.Length + Environment.NewLine.Length;
                if (_lines.Count > MaxLines || _characters > MaxCharacters)
                {
                    var removed = 0;
                    do
                    {
                        _characters -= _lines.Dequeue().Length + Environment.NewLine.Length;
                        removed++;
                    }
                    while (_lines.Count > 1 &&
                           (removed < TrimLines || _lines.Count > MaxLines || _characters > MaxCharacters));
                }

                _changed = true;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
            _characters = 0;
            _changed = true;
        }
    }

    public bool TryGetChangedText(out string text)
    {
        lock (_sync)
        {
            text = _changed ? BuildText() : string.Empty;
            var changed = _changed;
            _changed = false;
            return changed;
        }
    }

    public override string ToString()
    {
        lock (_sync)
        {
            return BuildText();
        }
    }

    private string BuildText() => _lines.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, _lines) + Environment.NewLine;
}
