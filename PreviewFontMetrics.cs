using System.Drawing;

namespace DeskLyric;

internal static class PreviewFontMetrics
{
    // The band's private font metrics are unavailable. Use a similar proportional font,
    // with a conservative nine-CJK-glyph width inside the known roughly ten-glyph row.
    private static readonly Font Font = new("Microsoft YaHei UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    internal static double Measure(string value) => TextRenderer.MeasureText(value, Font, Size.Empty, Flags).Width;
    internal static double Width => Measure("中中中中中中中中中");
    private static readonly Dictionary<string, string> Cache = new();

    internal static string Fit(string value)
    {
        if (Cache.TryGetValue(value, out string? result)) return result;
        result = StaticPreviewText.Fit(value, Measure, Width);
        if (Cache.Count >= 256) Cache.Clear();
        Cache[value] = result;
        return result;
    }
}
