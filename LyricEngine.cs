using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Devices;
using Windows.Devices.Enumeration;
using Timer = System.Windows.Forms.Timer;

namespace DeskLyric;

internal sealed class LyricEngine : IDisposable
{
    internal sealed record LrcLine(double Time, string Text);
    private static readonly Regex Tags = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex Credits = new(
        @"^(作词|作曲|编曲|制作人|制作|出品|监制|混音|母带|录音|吉他|贝斯|鼓|和声|弦乐|统筹|策划|发行|版权|演唱|原唱|翻唱|词|曲|OP|SP|ISRC|词曲)[^:：]{0,20}[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Timer _timer = new() { Interval = 250 };
    private readonly Config _config;
    private NeteaseLogReader _reader;
    private MediaPlayer? _player;
    private SystemMediaTransportControls? _smtc;
    private CancellationTokenSource? _lyricRequest;
    private List<LrcLine> _lines = new();
    private PlaybackSnapshot? _snapshot;
    private string _songId = "";
    private string _lastPayload = "";
    private string _lastSourceError = "";
    private string? _trackPath;
    private double _trackDuration;
    private long _nextSessionAttempt;
    private long _lastPush;
    private long _generation;
    private bool _loading;
    private bool _lyricsFailed;
    private long _retryLyricsAfter;
    private Task _sessionRelease = Task.CompletedTask;
    private Task _outputBinding = Task.CompletedTask;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "等待网易云";

    public LyricEngine(Config config)
    {
        _config = config;
        _reader = new(config.NeteaseLogPath);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskLyric/{Program.Version}");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (IsRunning) return;
        _reader = new(_config.NeteaseLogPath);
        IsRunning = true;
        Status = "正在读取网易云播放日志";
        _timer.Start();
        Program.Log("engine started; source=netease-elog");
        Tick();
    }

    public Task StopAsync()
    {
        IsRunning = false;
        _timer.Stop();
        _generation++;
        _lyricRequest?.Cancel();
        _lyricRequest?.Dispose();
        _lyricRequest = null;
        _loading = false;
        _songId = "";
        _snapshot = null;
        _lines.Clear();
        _lastPayload = "";
        Status = "已停止推送";
        return ReleaseSessionAsync();
    }

    public void RefreshDisplay(bool refreshOutput = false)
    {
        _lastPayload = "";
        if (refreshOutput && _player != null)
        {
            // A live AudioDevice change can leave the transport on the old endpoint.
            // Recreate only the output, retaining the song, lyrics and source clock.
            _ = ReleaseSessionAsync();
            _nextSessionAttempt = 0;
        }
        if (IsRunning)
        {
            try
            {
                if (_snapshot != null) ApplySnapshot(_snapshot);
                else Tick();
            }
            catch (Exception exception) { Program.Log("display refresh: " + exception); }
        }
    }

    private void Tick()
    {
        if (!IsRunning) return;
        try
        {
            PlaybackSnapshot? snapshot = _reader.Read();
            if (snapshot == null)
            {
                Status = _reader.LastError.Length > 0 ? "无法读取播放日志，请查看日志" : "等待播放记录，请在网易云切换一首歌";
                if (_reader.LastError != _lastSourceError)
                {
                    _lastSourceError = _reader.LastError;
                    if (_lastSourceError.Length > 0) Program.Log("source unavailable: " + _lastSourceError);
                }
                if (_snapshot != null)
                {
                    _snapshot = null;
                    _songId = "";
                    _lines.Clear();
                    _lastPayload = "";
                    _generation++;
                    _lyricRequest?.Cancel();
                    _loading = false;
                }
                _ = ReleaseSessionAsync();
                return;
            }
            ApplySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            Status = "推送遇到问题，请查看日志";
            Program.Log("engine error: " + exception.Message);
        }
    }

    private void ApplySnapshot(PlaybackSnapshot snapshot)
    {
        _lastSourceError = "";
        _snapshot = snapshot;
        Status = (snapshot.IsPlaying ? "播放中 · " : "已暂停 · ") + snapshot.Track.Title;
        if (_songId != snapshot.Track.Id)
        {
            _songId = snapshot.Track.Id;
            _lines.Clear();
            _lastPayload = "";
            _lyricsFailed = false;
            _ = ReleaseSessionAsync();
            _nextSessionAttempt = 0;
            Program.Log($"song id={_songId} title=[{snapshot.Track.Title}] source=netease-elog");
            BeginLyrics(snapshot.Track);
        }
        else if (!_loading && _lyricsFailed && Environment.TickCount64 >= _retryLyricsAfter)
            BeginLyrics(snapshot.Track);
        if (_smtc == null && _sessionRelease.IsCompleted && Environment.TickCount64 >= _nextSessionAttempt)
        {
            _nextSessionAttempt = Environment.TickCount64 + 5000;
            CreateSession(snapshot.Track.Duration);
        }
        if (_smtc == null || _player == null || !_outputBinding.IsCompleted) return;
        if (!_smtc.IsEnabled) _smtc.IsEnabled = true;
        SyncPlayback(snapshot);
        (string first, string second) = _config.NativeScrolling
            ? RenderNative(_lines, snapshot.Position, snapshot.Track, _config.ShowNextLyric, _config.StaticNextLyric)
            : RenderPage(_lines, snapshot.Position, snapshot.Track);
        if (!_config.NativeScrolling)
        {
            first = Limit(first, 9);
            second = Limit(second, 10);
            if (second.Length == 0) second = NativeScrollText.Blank;
        }
        string payload = first + "\u0001" + second;
        if (payload != _lastPayload || (!_config.NativeScrolling && Environment.TickCount64 - _lastPush >= 3000))
            Push(first, second, snapshot.IsPlaying);
    }

    private void BeginLyrics(TrackInfo track)
    {
        _lyricRequest?.Cancel();
        _lyricRequest?.Dispose();
        _lyricRequest = new();
        _loading = true;
        _lyricsFailed = false;
        long generation = ++_generation;
        _ = LoadLyrics(track.Id, generation, _lyricRequest.Token);
    }

    private async Task LoadLyrics(string id, long generation, CancellationToken cancellation)
    {
        try
        {
            string url = $"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1";
            using HttpResponseMessage response = await _http.GetAsync(url, cancellation);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            List<LrcLine> lines = new();
            if (document.RootElement.TryGetProperty("lrc", out JsonElement lrc) &&
                lrc.TryGetProperty("lyric", out JsonElement text))
                lines = ParseLrc(text.GetString() ?? "");
            if (!IsRunning || generation != _generation || cancellation.IsCancellationRequested) return;
            _lines = lines;
            _loading = false;
            Program.Log($"lyrics loaded id={id} lines={lines.Count}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsRunning || generation != _generation) return;
            _loading = false;
            _lyricsFailed = true;
            _retryLyricsAfter = Environment.TickCount64 + 15000;
            Program.Log($"lyrics failed id={id}: {exception.Message}");
        }
    }

    internal static List<LrcLine> ParseLrc(string text)
    {
        List<LrcLine> lines = new();
        foreach (string raw in text.Split('\n'))
        {
            MatchCollection matches = Tags.Matches(raw);
            if (matches.Count == 0) continue;
            string content = Tags.Replace(raw, "").Trim();
            if (content.Length == 0 || Credits.IsMatch(content)) continue;
            foreach (Match match in matches)
            {
                double time = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (match.Groups[3].Success)
                    time += int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) / Math.Pow(10, match.Groups[3].Length);
                lines.Add(new(time, content));
            }
        }
        return lines.OrderBy(line => line.Time).ToList();
    }

    internal static (string First, string Second) RenderNative(IReadOnlyList<LrcLine> lines, double position, TrackInfo track,
        bool showNextLyric = false, bool staticNextLyric = false)
    {
        int index = -1;
        for (int i = 0; i < lines.Count && lines[i].Time <= position + 0.05; i++) index = i;
        if (index < 0) return NativeScrollText.Fit(track.Title, track.Artist);

        LrcLine current = lines[index];
        if (showNextLyric)
        {
            if (staticNextLyric)
            {
                string preview = index + 1 < lines.Count ? PreviewFontMetrics.Fit(lines[index + 1].Text) : NativeScrollText.Blank;
                var segments = NativeScrollText.SplitPrimary(current.Text,
                    Math.Min(NativeScrollText.MaximumPrimaryBytes, NativeScrollText.MaximumBytes - Encoding.UTF8.GetByteCount(preview)));
                double until = index + 1 < lines.Count ? lines[index + 1].Time : current.Time + 5;
                double segmentDuration = Math.Max((until - current.Time) / segments.Count, 0.2);
                int segment = Math.Clamp((int)((position - current.Time) / segmentDuration), 0, segments.Count - 1);
                return (segments[segment], preview);
            }
            // Each row gets an independently scrolling, readable segment. Together these
            // remain inside the 20+20 CJK budget verified on the physical desk.
            var currentParts = NativeScrollText.SplitPrimary(current.Text, 60);
            var nextParts = index + 1 < lines.Count
                ? NativeScrollText.SplitPrimary(lines[index + 1].Text, 60)
                : new[] { NativeScrollText.Blank };
            double lineEnd = index + 1 < lines.Count ? lines[index + 1].Time : current.Time + 5;
            int count = Math.Max(currentParts.Count, nextParts.Count);
            double partDuration = Math.Max((lineEnd - current.Time) / count, 0.2);
            int page = Math.Clamp((int)((position - current.Time) / partDuration), 0, count - 1);
            return (currentParts[Math.Min(page, currentParts.Count - 1)], nextParts[Math.Min(page, nextParts.Count - 1)]);
        }
        string songInfo = NativeScrollText.SongInfo(track);
        IReadOnlyList<string> parts = NativeScrollText.SplitPrimary(current.Text,
            Math.Min(NativeScrollText.MaximumPrimaryBytes, NativeScrollText.MaximumBytes - Encoding.UTF8.GetByteCount(songInfo)));
        double end = index + 1 < lines.Count ? lines[index + 1].Time : current.Time + 5;
        double duration = Math.Max((end - current.Time) / parts.Count, 0.2);
        int part = Math.Clamp((int)((position - current.Time) / duration), 0, parts.Count - 1);
        return (parts[part], songInfo);
    }

    internal static (string First, string Second) RenderPage(IReadOnlyList<LrcLine> lines, double position, TrackInfo track)
    {
        int index = -1;
        for (int i = 0; i < lines.Count && lines[i].Time <= position + 0.05; i++) index = i;
        if (index < 0)
            return (track.Title.Length > 0 ? track.Title : "♪", track.Artist.Length > 0 ? track.Artist : "\u3000");
        LrcLine current = lines[index];
        // EnumerateRunes keeps emoji and other surrogate pairs intact when paging.
        string[] characters = current.Text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        int pageCount = Math.Max(1, (characters.Length + 18) / 19);
        double end = index + 1 < lines.Count ? lines[index + 1].Time : current.Time + 5;
        double pageDuration = Math.Max((end - current.Time) / pageCount, 0.2);
        int page = Math.Clamp((int)((position - current.Time) / pageDuration), 0, pageCount - 1);
        string[] visible = characters.Skip(page * 19).Take(19).ToArray();
        string first = string.Concat(visible.Take(9));
        string second = string.Concat(visible.Skip(9).Take(10));
        if (second.Length == 0 && index + 1 < lines.Count)
            second = Limit(lines[index + 1].Text, 10);
        return (first.Length > 0 ? first : "♪", second.Length > 0 ? second : "\u3000");
    }

    private static string Limit(string value, int maximum)
        => string.Concat(value.EnumerateRunes().Take(maximum).Select(rune => rune.ToString()));

    private void CreateSession(double duration)
    {
        _trackDuration = duration > 0 ? duration : 240;
        _trackPath = Path.Combine(Program.BaseDir, "desklyric-carrier.wav");
        WriteCarrier(_trackPath, _trackDuration);
        MediaPlayer player = new() { Volume = 0.3, IsLoopingEnabled = true, AudioCategory = MediaPlayerAudioCategory.Other };
        try
        {
            _smtc = player.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            _player = player;
            _outputBinding = BindDeskOutputAsync(player, _trackPath);
            _lastPayload = "";
            Program.Log("media output created");
        }
        catch
        {
            player.Dispose();
            _smtc = null;
            throw;
        }
    }

    private async Task BindDeskOutputAsync(MediaPlayer player, string trackPath)
    {
        try
        {
            var endpoints = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            if (!ReferenceEquals(_player, player)) return;
            var desktop = endpoints.FirstOrDefault(device => device.Name.Contains("小米手环多功能桌搭", StringComparison.OrdinalIgnoreCase));
            if (desktop == null)
            {
                Program.Log("carrier: desktop audio endpoint unavailable; using default output");
            }
            else
            {
                player.AudioDevice = desktop;
                Program.Log("carrier routed to " + desktop.Name);
            }
        }
        catch (Exception exception) { Program.Log($"output selection 0x{exception.HResult:X8}: {exception.Message}"); }
        if (!ReferenceEquals(_player, player)) return;
        try
        {
            // Bind the endpoint before opening the source, in both display modes.
            player.Source = MediaSource.CreateFromUri(new Uri(trackPath));
            _lastPayload = "";
        }
        catch (Exception exception)
        {
            Program.Log("media source initialization: " + exception);
            _ = ReleaseSessionAsync();
            _nextSessionAttempt = Environment.TickCount64 + 5000;
        }
    }

    private void SyncPlayback(PlaybackSnapshot snapshot)
    {
        if (_player == null || _smtc == null || !_outputBinding.IsCompleted) return;
        MediaPlaybackSession session = _player.PlaybackSession;
        if (snapshot.IsPlaying && session.PlaybackState != MediaPlaybackState.Playing) _player.Play();
        if (!snapshot.IsPlaying && session.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        double position = Math.Min(snapshot.Position, Math.Max(0, _trackDuration - 0.1));
        if (!_config.NativeScrolling && (session.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused) &&
            Math.Abs(session.Position.TotalSeconds - position) > 1.5)
            session.Position = TimeSpan.FromSeconds(position);
        MediaPlaybackStatus status = snapshot.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (_smtc.PlaybackStatus != status) _smtc.PlaybackStatus = status;
    }

    private void Push(string first, string second, bool playing)
    {
        if (_smtc == null) return;
        if (!_outputBinding.IsCompleted || _player?.PlaybackSession.PlaybackState is not (MediaPlaybackState.Playing or MediaPlaybackState.Paused)) return;
        if (_config.NativeScrolling)
        {
            if (!_outputBinding.IsCompleted) return;
            // Wait for the carrier to open so its automatic metadata does not replace the
            // first lyric. Repeated identical metadata updates can restart a native marquee.
            if (_player?.PlaybackSession.PlaybackState is not (MediaPlaybackState.Playing or MediaPlaybackState.Paused)) return;
            (first, second) = NativeScrollText.Fit(first, second);
        }
        else
        {
            first = Limit(first, 9);
            second = Limit(second, 10);
        }
        if (second.Length == 0) second = "\u3000";
        SystemMediaTransportControlsDisplayUpdater updater = _smtc.DisplayUpdater;
        string payload = first + "\u0001" + second;
        if (_config.NativeScrolling && payload == _lastPayload &&
            updater.MusicProperties.Title == first && updater.MusicProperties.Artist == second)
        {
            MediaPlaybackStatus repeatedStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (_smtc.PlaybackStatus != repeatedStatus) _smtc.PlaybackStatus = repeatedStatus;
            return;
        }
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = first;
        updater.MusicProperties.Artist = second;
        updater.Update();
        MediaPlaybackStatus status = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (_smtc.PlaybackStatus != status) _smtc.PlaybackStatus = status;
        if (payload != _lastPayload)
            Program.Log($"push [{first}] [{second}] position={_snapshot?.Position:0.00}");
        _lastPayload = payload;
        _lastPush = Environment.TickCount64;
    }

    private static void WriteCarrier(string path, double seconds)
    {
        int samples = (int)(Math.Clamp(seconds, 1, 3600) * 8000);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (int sample = 0; sample < samples; sample++)
            writer.Write((short)(300 * Math.Sin(2 * Math.PI * 440 * sample / 8000)));
    }

    private Task ReleaseSessionAsync()
    {
        MediaPlayer? player = _player;
        SystemMediaTransportControls? controls = _smtc;
        string? trackPath = _trackPath;
        _player = null;
        _smtc = null;
        _trackPath = null;
        if (player == null && controls == null && trackPath == null) return _sessionRelease;
        _sessionRelease = CloseSessionAsync(player, controls, trackPath);
        return _sessionRelease;
    }

    private static async Task CloseSessionAsync(MediaPlayer? player, SystemMediaTransportControls? controls, string? trackPath)
    {
        try
        {
            player?.Pause();
            if (player != null) player.Source = null;
        }
        catch (Exception exception) { Program.Log("media pause during close: " + exception.Message); }
        try
        {
            if (controls != null)
            {
                controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                SystemMediaTransportControlsDisplayUpdater updater = controls.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = "";
                updater.MusicProperties.Artist = "";
                updater.Update();
                Program.Log("media stopped; metadata cleared");
                // SMTC and Bluetooth forward changes asynchronously. Keep the session alive for
                // the terminal updates, then disable it after the receiver has had time to observe them.
                await Task.Delay(500).ConfigureAwait(false);
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.Update();
                controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                Program.Log("media closed");
                await Task.Delay(200).ConfigureAwait(false);
                controls.IsEnabled = false;
            }
        }
        catch (Exception exception) { Program.Log("media terminal update: " + exception.Message); }
        finally
        {
            try
            {
                if (player != null) player.Source = null;
            }
            catch (Exception exception) { Program.Log("media source close: " + exception.Message); }
            try { player?.Dispose(); }
            catch (Exception exception) { Program.Log("media dispose: " + exception.Message); }
            if (trackPath != null)
            {
                try { File.Delete(trackPath); }
                catch (IOException exception) { Program.Log("carrier cleanup: " + exception.Message); }
                catch (UnauthorizedAccessException exception) { Program.Log("carrier cleanup: " + exception.Message); }
            }
            Program.Log("media output released");
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _timer.Dispose();
        _http.Dispose();
    }
}
