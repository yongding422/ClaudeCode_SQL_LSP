namespace SqlLsp.Core.Tests;

public class SourceTextTests
{
    [Fact]
    public void Positions_edits_crlf_tabs_surrogates_and_eof_use_utf16()
    {
        var source = new SourceText("a😀\t中\r\nsecond\n");
        Assert.Equal(new TextPosition(0, 5), source.PositionAt(5));
        Assert.Equal(new TextPosition(1, 0), source.PositionAt(7));
        Assert.Equal(new TextPosition(2, 0), source.PositionAt(source.Text.Length));
        Assert.Equal(5, source.OffsetAt(new TextPosition(0, 999)));
        var edited = source.Apply(new[]
        {
            new TextEdit("x", new TextRange(new(0, 1), new(0, 3))),
            new TextEdit("!", new TextRange(new(0, 4), new(1, 0)))
        });
        Assert.Equal("ax\t中!second\n", edited.Text);
        Assert.Throws<ArgumentException>(() => source.OffsetAt(new(0, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.OffsetAt(new(9, 0)));
        Assert.Throws<ArgumentException>(() => source.Apply([new("", new TextRange(new(1, 2), new(0, 0)))]));
    }

    [Fact]
    public void Full_replacement_and_sequential_incremental_edits_apply_atomically()
    {
        var result = new SourceText("old").Apply([
            new("SELECT 1;"),
            new("2", new TextRange(new(0, 7), new(0, 8))),
            new("\nGO", new TextRange(new(0, 9), new(0, 9)))
        ]);
        Assert.Equal("SELECT 2;\nGO", result.Text);
    }
}
