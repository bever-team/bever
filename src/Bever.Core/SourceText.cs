namespace Bever.Core;

public sealed record SourceText(string Path, string Text)
{
    public IReadOnlyList<SourceLine> Lines { get; } = BuildLines(Text);

    public static SourceText FromFile(string path) => new(path, File.ReadAllText(path));

    private static IReadOnlyList<SourceLine> BuildLines(string text)
    {
        var lines = new List<SourceLine>();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var start = 0;
        var lineNumber = 1;

        foreach (var line in normalized.Split('\n'))
        {
            lines.Add(new SourceLine(lineNumber++, start, line));
            start += line.Length + 1;
        }

        return lines;
    }
}

public sealed record SourceLine(int LineNumber, int Start, string Text);
