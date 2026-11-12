using System.Text.RegularExpressions;

namespace VideoEdit.Services;

public static class ScriptPassage
{
    private static readonly Regex Marker = new(
        @"<!--\s*generate\s*-->(.*?)<!--\s*/generate\s*-->",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlComment = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static string ReadMarked(string markdown)
    {
        var matches = Marker.Matches(markdown ?? "");
        if (matches.Count == 0)
        {
            throw VideoEditException.Validation(
                "sources/script.md has no marked passage. Wrap the M1 text in <!-- generate --> … <!-- /generate -->.");
        }

        if (matches.Count > 1)
        {
            throw VideoEditException.Validation(
                "sources/script.md has more than one marked passage. Leave exactly one <!-- generate --> block.");
        }

        var text = matches[0].Groups[1].Value.Trim();
        if (text.Length == 0)
        {
            throw VideoEditException.Validation("The marked passage in sources/script.md is empty.");
        }

        return text;
    }

    public static string ReadHeading(string markdown)
    {
        foreach (var line in StripComments(markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                return line.TrimStart('#').Trim();
            }
        }

        return "";
    }

    public static string ReadAuthoritative(string markdown)
    {
        var body = new List<string>();
        var skippedHeading = false;
        foreach (var line in StripComments(markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (!skippedHeading)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                skippedHeading = true;
                if (line.StartsWith('#'))
                {
                    continue;
                }
            }

            body.Add(line);
        }

        var text = Regex.Replace(string.Join('\n', body).Trim(), @"\n{3,}", "\n\n");
        if (text.Length == 0)
        {
            throw VideoEditException.Validation("sources/script.md has no authoritative script body.");
        }

        return text;
    }

    public static string StripComments(string markdown) =>
        HtmlComment.Replace(markdown, "");
}
