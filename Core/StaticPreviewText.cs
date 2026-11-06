using System.Globalization;
using System.Text;

namespace DeskLyric;

internal static class StaticPreviewText
{
    internal static string Fit(string text, Func<string, double> measure, double width, int maximumBytes = 60)
    {
        if (string.IsNullOrWhiteSpace(text)) return NativeScrollText.Blank;
        bool Fits(string value) => Encoding.UTF8.GetByteCount(value) <= maximumBytes && measure(value) <= width;
        if (Fits(text)) return text;
        const string ellipsis = "…";
        string prefix = "";
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string candidate = prefix + elements.GetTextElement();
            if (!Fits(candidate + ellipsis)) break;
            prefix = candidate;
        }
        if (prefix.Length < text.Length && prefix.Length > 0 &&
            char.IsLetter(text[prefix.Length - 1]) && text[prefix.Length - 1] < 128 &&
            char.IsLetter(text[prefix.Length]) && text[prefix.Length] < 128)
        {
            int space = prefix.LastIndexOfAny(new[] { ' ', '\t' });
            if (space > 0) prefix = prefix[..space];
        }
        return prefix.TrimEnd() + ellipsis;
    }
}
