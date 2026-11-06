using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskLyric;

internal static class NeteaseLogCodec
{
    // The client substitutes each log byte. Decode complete lines before UTF-8 conversion.
    internal static readonly byte[] DecodeTable = BuildTable();
    internal static readonly byte EncodedNewline = (byte)Array.IndexOf(DecodeTable, (byte)'\n');
    private static readonly Regex Header = new(
        @"^\[(\d+):\d+:\d{4}/\d{6}:(\d+):[A-Z]+:[\w.\-]+\(\d+\)\](?:\s+\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\])?",
        RegexOptions.Compiled);
    private static readonly Regex Seek = new("\"setPlayingPosition\"\\s*,\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.Compiled);
    private static readonly Regex Status = new("\"native[^\"]*state\"\\s*,\\s*(\\d+)", RegexOptions.Compiled);
    private static readonly Regex Exited = new(@"Process\s+(\d+)\s+exited", RegexOptions.Compiled);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        byte[] decoded = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) decoded[i] = DecodeTable[bytes[i]];
        return Encoding.UTF8.GetString(decoded);
    }

    public static PlaybackEvent? Parse(string line, long nowTicks, DateTimeOffset now)
    {
        Match header = Header.Match(line);
        if (!header.Success || !int.TryParse(header.Groups[1].Value, out int pid) ||
            !long.TryParse(header.Groups[2].Value, out long ticks)) return null;
        // Older clients use a 32-bit uptime counter, which wraps every 49.7 days.
        if (ticks <= uint.MaxValue)
            ticks = nowTicks - unchecked((uint)nowTicks - (uint)ticks);
        if (header.Groups[3].Success && DateTime.TryParseExact(header.Groups[3].Value,
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime wallTime))
        {
            long wallTicks = nowTicks - (long)(now - new DateTimeOffset(wallTime)).TotalMilliseconds;
            if (Math.Abs(wallTicks - ticks) > 5000) ticks = wallTicks;
        }
        if (ticks < 0 || ticks > nowTicks + 5000) return null;
        if (line.Contains("exitApp", StringComparison.Ordinal))
            return new(PlaybackEventKind.Exit, ticks, pid);
        Match exited = Exited.Match(line);
        if (exited.Success && int.TryParse(exited.Groups[1].Value, out int exitedPid))
            return new(PlaybackEventKind.Exit, ticks, pid, ExitedProcessId: exitedPid);
        Match seek = Seek.Match(line);
        if (seek.Success && double.TryParse(seek.Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double position) && double.IsFinite(position))
            return new(PlaybackEventKind.Seek, ticks, pid, Position: position);
        Match status = Status.Match(line);
        if (status.Success && int.TryParse(status.Groups[1].Value, out int state))
            return new(PlaybackEventKind.Status, ticks, pid, Status: state);

        bool metadata = line.Contains("\"checkPlayPrivilege\"", StringComparison.Ordinal);
        bool play = line.Contains("\"playOneTrackInPlayingList\"", StringComparison.Ordinal);
        bool loaded = line.Contains("\"native", StringComparison.Ordinal) &&
            line.Contains("load完成", StringComparison.Ordinal) && line.Contains("\"songId\"", StringComparison.Ordinal);
        if (!metadata && !play && !loaded) return null;
        int start = line.IndexOf('{'), end = line.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line.Substring(start, end - start + 1));
            JsonElement data = document.RootElement;
            if (data.TryGetProperty("track", out JsonElement nested)) data = nested;
            string id = ReadString(data, loaded ? "songId" : "id");
            if (!long.TryParse(id, out long numericId) || numericId <= 0) return null;
            string title = ReadString(data, "name");
            string artist = "";
            if (data.TryGetProperty("artists", out JsonElement artists) && artists.ValueKind == JsonValueKind.Array)
                artist = string.Join("/", artists.EnumerateArray().Select(item => ReadString(item, "name")));
            double duration = 0;
            string durationText = ReadString(data, loaded ? "songDuration" : "duration");
            if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds))
                duration = Math.Max(0, milliseconds / 1000);
            return new(loaded ? PlaybackEventKind.Loaded : play ? PlaybackEventKind.PlayTrack : PlaybackEventKind.Metadata,
                ticks, pid, new TrackInfo(id, title, artist, duration));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static byte[] BuildTable()
    {
        byte[] table = new byte[256];
        for (int value = 0; value < 256; value++)
        {
            int high = value >> 4, low = value & 15;
            table[value] = (byte)(((high ^ ((low + 8) % 16)) * 16 + (value >> 6) * 4 + ((~high) & 3)) & 255);
        }
        return table;
    }
}
