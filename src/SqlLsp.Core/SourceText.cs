namespace SqlLsp.Core;

public readonly record struct TextPosition(int Line, int Character);
public readonly record struct TextRange(TextPosition Start, TextPosition End);
public sealed record TextEdit(string Text, TextRange? Range = null);

// .NET string offsets and LSP UTF-16 characters use the same code units.
public sealed class SourceText
{
    private readonly int[] lineStarts;
    public string Text { get; }

    public SourceText(string text)
    {
        Text = text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n') starts.Add(i + 1);
        }
        lineStarts = starts.ToArray();
    }

    public TextPosition PositionAt(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        var line = Array.BinarySearch(lineStarts, offset);
        if (line < 0) line = ~line - 1;
        return new TextPosition(line, Math.Min(offset, LineEnd(line)) - lineStarts[line]);
    }

    public int OffsetAt(TextPosition position)
    {
        if (position.Line < 0 || position.Line >= lineStarts.Length || position.Character < 0)
            throw new ArgumentOutOfRangeException(nameof(position), "Invalid UTF-16 position.");
        var offset = lineStarts[position.Line] + Math.Min(position.Character, LineEnd(position.Line) - lineStarts[position.Line]);
        if (offset > 0 && offset < Text.Length && char.IsHighSurrogate(Text[offset - 1]) && char.IsLowSurrogate(Text[offset]))
            throw new ArgumentException("An edit cannot split a surrogate pair.", nameof(position));
        return offset;
    }

    public TextRange Range(int start, int length) => new(PositionAt(start), PositionAt(start + length));

    public SourceText Apply(IEnumerable<TextEdit> edits)
    {
        var source = this;
        foreach (var edit in edits)
        {
            if (edit.Range is not { } range) { source = new SourceText(edit.Text); continue; }
            var start = source.OffsetAt(range.Start);
            var end = source.OffsetAt(range.End);
            if (end < start) throw new ArgumentException("Edit range ends before its start.", nameof(edits));
            source = new SourceText(source.Text[..start] + edit.Text + source.Text[end..]);
        }
        return source;
    }

    private int LineEnd(int line)
    {
        var end = line + 1 < lineStarts.Length ? lineStarts[line + 1] : Text.Length;
        while (end > lineStarts[line] && Text[end - 1] is '\r' or '\n') end--;
        return end;
    }
}
