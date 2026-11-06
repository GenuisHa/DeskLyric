using System.Text;
using System.Text.Json;

namespace DeskLyric;

internal static class Diagnostics
{
    public static async Task Run(string[] args)
    {
        string output = args.FirstOrDefault(value => value.StartsWith("--output="))?[9..]
            ?? Path.Combine(Program.BaseDir, "playback-diagnostic.jsonl");
        string? limit = args.FirstOrDefault(value => value.StartsWith("--seconds="))?[10..];
        int seconds = int.TryParse(limit, out int requested) ? Math.Clamp(requested, 1, 3600) : 10;
        NeteaseLogReader reader = new(Program.LoadConfig().NeteaseLogPath);
        using StreamWriter writer = new(output, false, new UTF8Encoding(false)) { AutoFlush = true };
        for (int index = 0; index < seconds; index++)
        {
            try
            {
                PlaybackSnapshot? snapshot = reader.Read();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now,
                    source = "netease-elog",
                    available = snapshot != null,
                    songId = snapshot?.Track.Id,
                    title = snapshot?.Track.Title,
                    artist = snapshot?.Track.Artist,
                    position = snapshot?.Position,
                    duration = snapshot?.Track.Duration,
                    playing = snapshot?.IsPlaying,
                    revision = snapshot?.Revision,
                    error = reader.LastError
                }));
            }
            catch (Exception exception)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { error = exception.ToString() }));
            }
            if (index + 1 < seconds) await Task.Delay(1000);
        }
    }
}
