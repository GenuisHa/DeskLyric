namespace DeskLyric;

internal sealed record TrackInfo(string Id, string Title, string Artist, double Duration);

internal sealed record PlaybackSnapshot(
    TrackInfo Track, double Position, bool IsPlaying, int ProcessId, long Revision);

internal enum PlaybackEventKind { Metadata, PlayTrack, Loaded, Seek, Status, Exit }

internal sealed record PlaybackEvent(
    PlaybackEventKind Kind, long Ticks, int ProcessId,
    TrackInfo? Track = null, double Position = 0, int Status = 0, int ExitedProcessId = 0);

internal sealed class PlaybackState
{
    private readonly Dictionary<string, TrackInfo> _knownTracks = new();
    private TrackInfo? _track;
    private double _position;
    private long _anchorTicks;
    private long _lastEventTicks;
    private bool _playing;
    private bool _waitingForLoad;
    private int _processId;
    private long _revision;
    private long _lastPlayTicks = -1;
    private long _lastLoadTicks = -1;

    public PlaybackSnapshot? Snapshot(long nowTicks)
    {
        return _track == null ? null : new PlaybackSnapshot(
            _track, PositionAt(nowTicks), _playing, _processId, _revision);
    }

    public void Reset()
    {
        _knownTracks.Clear();
        _track = null;
        _position = 0;
        _anchorTicks = _lastEventTicks = 0;
        _playing = _waitingForLoad = false;
        _processId = 0;
        _lastPlayTicks = _lastLoadTicks = -1;
        _revision++;
    }

    public void Apply(PlaybackEvent item)
    {
        if (item.Kind == PlaybackEventKind.Metadata)
        {
            Remember(item.Track);
            return;
        }
        if (item.Kind == PlaybackEventKind.Exit)
        {
            if (item.ExitedProcessId == _processId ||
                (item.ExitedProcessId == 0 && item.ProcessId == _processId))
                Reset();
            return;
        }
        // Different logging threads can write a little out of order.
        if (item.Ticks + 1000 < _lastEventTicks)
            return;
        long ticks = Math.Max(item.Ticks, _lastEventTicks);
        switch (item.Kind)
        {
            case PlaybackEventKind.PlayTrack when item.Track != null:
                Remember(item.Track);
                if (_lastPlayTicks == item.Ticks && _track?.Id == item.Track.Id)
                    return;
                _track = item.Track;
                _position = 0;
                _anchorTicks = ticks;
                _playing = false;
                _waitingForLoad = true;
                _processId = item.ProcessId;
                _lastPlayTicks = item.Ticks;
                _revision++;
                break;
            case PlaybackEventKind.Loaded when item.Track != null:
                if (_lastLoadTicks == item.Ticks && _track?.Id == item.Track.Id)
                    return;
                TrackInfo loaded = _knownTracks.GetValueOrDefault(item.Track.Id) ?? item.Track;
                bool changed = _track?.Id != loaded.Id;
                bool ended = _track != null && _track.Duration > 0 &&
                    PositionAt(ticks) >= _track.Duration - 0.5;
                double position = changed || _waitingForLoad || ended ? 0 : PositionAt(ticks);
                if (changed || ended)
                    _revision++;
                _track = loaded;
                _position = position;
                _anchorTicks = ticks;
                _playing = true;
                _waitingForLoad = false;
                _processId = item.ProcessId;
                _lastLoadTicks = item.Ticks;
                break;
            case PlaybackEventKind.Seek when _track != null:
                _position = Math.Clamp(item.Position, 0, _track.Duration > 0 ? _track.Duration : double.MaxValue);
                _anchorTicks = ticks;
                _processId = item.ProcessId;
                break;
            case PlaybackEventKind.Status when _track != null && item.Status is 1 or 2:
                _position = PositionAt(ticks);
                _anchorTicks = ticks;
                _playing = item.Status == 1;
                _processId = item.ProcessId;
                break;
        }
        _lastEventTicks = ticks;
    }

    private double PositionAt(long ticks)
    {
        double position = _position + (_playing ? Math.Max(0, ticks - _anchorTicks) / 1000.0 : 0);
        return Math.Clamp(position, 0, _track?.Duration > 0 ? _track.Duration : double.MaxValue);
    }

    private void Remember(TrackInfo? track)
    {
        if (track == null) return;
        if (_knownTracks.Count > 64) _knownTracks.Clear();
        _knownTracks[track.Id] = track;
        if (_track?.Id == track.Id) _track = track;
    }
}
