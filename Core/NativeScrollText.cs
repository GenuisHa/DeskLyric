using System.Text;

namespace DeskLyric;

internal static class NativeScrollText
{
    // Transport safety, not the on-screen width. The device measures its own glyphs.
    internal const int MaximumBytes = 120;
    internal const string Blank = "\u3000";
    internal const int MaximumPrimaryBytes = MaximumBytes - 3;

    internal static (string First, string Second) Fit(string first, string second)
    {
        first = Shorten(string.IsNullOrEmpty(first) ? "♪" : first, MaximumPrimaryBytes);
        int available = MaximumBytes - Encoding.UTF8.GetByteCount(first);
        second = string.IsNullOrEmpty(second) ? Blank
            : Encoding.UTF8.GetByteCount(second) <= available ? second
            : available < 9 ? Blank : Shorten(second, available);
        return (first, second);
    }

    internal static string SongInfo(TrackInfo track)
    {
        string info = string.Join(" - ", new[] { track.Title, track.Artist }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrEmpty(info) ? Blank : Shorten(info, 60);
    }

    internal static IReadOnlyList<string> SplitPrimary(string text, int maximumBytes = MaximumPrimaryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 4);
        List<string> parts = new();
        int start = 0;
        while (start < text.Length)
        {
            int end = start, bytes = 0;
            foreach (Rune rune in text.AsSpan(start).EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > maximumBytes) break;
                bytes += rune.Utf8SequenceLength;
                end += rune.Utf16SequenceLength;
            }
            // If transport segmentation is required, prefer an English word boundary.
            // Keep the whitespace itself so concatenating the segments restores all text.
            if (end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsWhiteSpace(text[end - 1]))
            {
                int space = text.LastIndexOfAny(new[] { ' ', '\t' }, end - 1, end - start);
                if (space >= start && Encoding.UTF8.GetByteCount(text.AsSpan(start, space + 1 - start)) >= maximumBytes / 2)
                    end = space + 1;
            }
            parts.Add(text[start..end]);
            start = end;
        }
        if (parts.Count == 0) parts.Add("♪");
        return parts;
    }

    private static string Shorten(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes) return text;
        StringBuilder shortened = new();
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes - 3) break;
            shortened.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return shortened.Append('…').ToString();
    }
}
