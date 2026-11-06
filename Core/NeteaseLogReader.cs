using System.Diagnostics;

namespace DeskLyric;

internal sealed class NeteaseLogReader
{
    private const int MaximumPreload = 16 * 1024 * 1024;
    private const int MaximumLine = 1024 * 1024;
    private readonly string _path;
    private readonly PlaybackState _state = new();
    private readonly MemoryStream _partialLine = new();
    private long _offset;
    private byte[]? _prefix;
    private DateTime _creation;
    private bool _loaded;
    private long _lastProcessCheck;
    private HashSet<int> _liveProcesses = new();
    public string LastError { get; private set; } = "";
    public string Path => _path;

    public NeteaseLogReader(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetEase", "CloudMusic", "cloudmusic.elog")
            : path;
    }

    public PlaybackSnapshot? Read()
    {
        try
        {
            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            DateTime creation = File.GetCreationTimeUtc(_path);
            byte[] prefix = new byte[(int)Math.Min(length, 128)];
            stream.ReadExactly(prefix);
            bool prefixChanged = _prefix != null && prefix.Length >= _prefix.Length &&
                !prefix.AsSpan(0, _prefix.Length).SequenceEqual(_prefix);
            if (!_loaded || length < _offset || creation != _creation || prefixChanged)
            {
                _state.Reset();
                _partialLine.SetLength(0);
                _offset = Math.Max(0, length - MaximumPreload);
                _prefix = prefix;
                _creation = creation;
                _loaded = true;
            }
            if (_prefix == null || prefix.Length > _prefix.Length) _prefix = prefix;
            stream.Position = _offset;
            byte[] buffer = new byte[64 * 1024];
            long nowTicks = Environment.TickCount64;
            DateTimeOffset now = DateTimeOffset.Now;
            while (stream.Position < length)
            {
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - stream.Position));
                if (count == 0) break;
                Ingest(buffer.AsSpan(0, count), nowTicks, now);
                _offset = stream.Position;
            }
            LastError = "";
            if (nowTicks - _lastProcessCheck > 1000 || _lastProcessCheck == 0)
            {
                _liveProcesses = Process.GetProcessesByName("cloudmusic").Select(process =>
                {
                    using (process) return process.Id;
                }).ToHashSet();
                _lastProcessCheck = nowTicks;
            }
            PlaybackSnapshot? snapshot = _state.Snapshot(nowTicks);
            return snapshot != null && _liveProcesses.Contains(snapshot.ProcessId) ? snapshot : null;
        }
        catch (IOException exception) { LastError = exception.Message; }
        catch (UnauthorizedAccessException exception) { LastError = exception.Message; }
        // A lost log source must not leave a previously playing clock running forever.
        _loaded = false;
        return null;
    }

    private void Ingest(ReadOnlySpan<byte> buffer, long ticks, DateTimeOffset now)
    {
        int start = 0;
        for (int index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != NeteaseLogCodec.EncodedNewline) continue;
            _partialLine.Write(buffer.Slice(start, index - start));
            if (_partialLine.Length <= MaximumLine)
            {
                string line = NeteaseLogCodec.Decode(_partialLine.GetBuffer().AsSpan(0, (int)_partialLine.Length));
                PlaybackEvent? item = NeteaseLogCodec.Parse(line, ticks, now);
                if (item != null) _state.Apply(item);
            }
            _partialLine.SetLength(0);
            start = index + 1;
        }
        _partialLine.Write(buffer[start..]);
        if (_partialLine.Length > MaximumLine) _partialLine.SetLength(0);
    }
}
