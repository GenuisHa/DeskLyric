namespace DeskLyric;

internal sealed class Config
{
    public bool AutoFollow { get; set; } = true;
    public bool AutoStartWithWindows { get; set; }
    public string? NeteaseLogPath { get; set; }
    public bool NativeScrolling { get; set; } = true;
    public bool ShowNextLyric { get; set; } = true;
    public bool StaticNextLyric { get; set; }
}
