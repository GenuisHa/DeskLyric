using System.Text;
using System.Text.Json;
using DeskLyric;

int checks = 0;
void Equal(double expected, double? actual, string label)
{
    if (!actual.HasValue || Math.Abs(expected - actual.Value) > 0.001)
        throw new Exception($"{label}: expected {expected}, got {actual}");
    checks++;
}
void Check(bool valid, string label)
{
    if (!valid) throw new Exception(label);
    checks++;
}

TrackInfo first = new("1", "测试歌曲🙂", "歌手", 180);
TrackInfo second = new("2", "另一首", "歌手", 300);
PlaybackState state = new();
state.Apply(new(PlaybackEventKind.Metadata, 1000, 10, first));
Check(state.Snapshot(1000) == null, "privilege checks must not activate a track");
state.Apply(new(PlaybackEventKind.PlayTrack, 1000, 10, first));
state.Apply(new(PlaybackEventKind.Loaded, 1500, 10, new("1", "", "", 0)));
Equal(10, state.Snapshot(11500)?.Position, "clock starts at native load, excluding buffer time");
state.Apply(new(PlaybackEventKind.Metadata, 12000, 10, second));
Check(state.Snapshot(12000)?.Track.Id == "1", "prefetch must not replace current track");
state.Apply(new(PlaybackEventKind.Status, 21500, 10, Status: 2));
Equal(20, state.Snapshot(51500)?.Position, "pause freezes position");
state.Apply(new(PlaybackEventKind.Seek, 51500, 10, Position: 60));
Equal(60, state.Snapshot(61500)?.Position, "seek during pause stays frozen");
state.Apply(new(PlaybackEventKind.Status, 61500, 10, Status: 1));
Equal(70, state.Snapshot(71500)?.Position, "resume advances from seek position");
state.Apply(new(PlaybackEventKind.Seek, 71500, 10, Position: 100));
Equal(105, state.Snapshot(76500)?.Position, "seek during playback rebases clock");
state.Apply(new(PlaybackEventKind.Loaded, 76500, 10, first));
Equal(106, state.Snapshot(77500)?.Position, "same-track quality reload preserves progress");
state.Apply(new(PlaybackEventKind.PlayTrack, 80000, 10, first));
state.Apply(new(PlaybackEventKind.Loaded, 80500, 10, first));
Equal(1, state.Snapshot(81500)?.Position, "explicit replay resets same-track position");
state.Apply(new(PlaybackEventKind.Loaded, 260500, 10, first));
Equal(1, state.Snapshot(261500)?.Position, "single repeat resets at track end");
state.Apply(new(PlaybackEventKind.Exit, 262000, 99, ExitedProcessId: 99));
Check(state.Snapshot(262000) != null, "unrelated renderer exit must not clear playback");
state.Apply(new(PlaybackEventKind.PlayTrack, 270000, 10, second));
state.Apply(new(PlaybackEventKind.Loaded, 270100, 10, second));
state.Apply(new(PlaybackEventKind.Seek, 10000, 10, Position: 140));
Equal(1, state.Snapshot(271100)?.Position, "old events must not contaminate new track");
state.Apply(new(PlaybackEventKind.Exit, 271100, 99, ExitedProcessId: 10));
Check(state.Snapshot(271100) == null, "active process exit clears playback");

string trackJson = JsonSerializer.Serialize(new { track = new { id = 1234, name = "测试歌曲🙂", duration = 180000, artists = new[] { new { name = "歌手" } } } });
string prefix = "[10:2:1105/145100:123456:INFO:app.js(7)] ";
string line = prefix + "[\"playOneTrackInPlayingList\"," + trackJson + "]\n";
byte[] reverse = new byte[256];
for (int i = 0; i < 256; i++) reverse[NeteaseLogCodec.DecodeTable[i]] = (byte)i;
byte[] encoded = Encoding.UTF8.GetBytes(line).Select(value => reverse[value]).ToArray();
Check(NeteaseLogCodec.Decode(encoded) == line, "log byte decoding preserves Unicode");
PlaybackEvent? parsed = NeteaseLogCodec.Parse(line, 130000, DateTimeOffset.Now);
Check(parsed?.Track?.Title == "测试歌曲🙂" && parsed.Track.Id == "1234", "parses actual track event shape");
Equal(180, parsed?.Track?.Duration, "duration is converted from milliseconds");
parsed = NeteaseLogCodec.Parse(prefix + "[\"setPlayingPosition\",45.486]", 130000, DateTimeOffset.Now);
Equal(45.486, parsed?.Position, "fractional seek positions are preserved");
parsed = NeteaseLogCodec.Parse(prefix + "[\"native播放资源load完成，开始播放\",{\"songId\":\"1234\",\"songDuration\":180000}]", 130000, DateTimeOffset.Now);
Check(parsed?.Kind == PlaybackEventKind.Loaded, "load-start event is not confused with load completion");
Check(NeteaseLogCodec.Parse(prefix + "[\"native播放资源开始load\",{\"songId\":\"1234\"}]", 130000, DateTimeOffset.Now) == null, "buffer start does not start playback clock");
Check(NeteaseLogCodec.Parse(prefix + "[\"playOneTrackInPlayingList\",{bad]", 130000, DateTimeOffset.Now) == null, "malformed records are ignored");
parsed = NeteaseLogCodec.Parse("[10:2:1105/145100:1000:INFO:app.js(7)] [\"setPlayingPosition\",9]", (long)uint.MaxValue + 2000, DateTimeOffset.Now);
Equal((long)uint.MaxValue + 1001, parsed?.Ticks, "32-bit uptime wrap is handled");
string nativeTitle = "平滑滚动测试，这是一整句超过屏幕宽度的歌词，请观察最后的文字";
string nativeArtist = "第二行也发送完整内容，观察是否能够平滑移动到最后";
var nativeFrame = NativeScrollText.Fit(nativeTitle, nativeArtist);
Check(nativeFrame.First == nativeTitle, "native scrolling preserves the complete 30-character current lyric");
Check(Encoding.UTF8.GetByteCount(nativeFrame.First + nativeFrame.Second) <= NativeScrollText.MaximumBytes,
    "long two-line metadata stays within the verified compatibility budget");
Check(nativeFrame.Second.EndsWith('…'), "a shortened preview is marked instead of silently losing text");
string twenty = "新的第一行测试这是一段长度较长的歌词内容";
nativeFrame = NativeScrollText.Fit(twenty, twenty);
Check(nativeFrame.First == twenty && nativeFrame.Second == twenty, "both 20-character scrolling lines remain complete");
nativeFrame = NativeScrollText.Fit("测试🙂中文", "下一句🎵歌词");
Check(nativeFrame.First == "测试🙂中文" && nativeFrame.Second == "下一句🎵歌词", "native metadata preserves emoji and Unicode");
nativeFrame = NativeScrollText.Fit("", "");
Check(nativeFrame.First == "♪" && nativeFrame.Second == NativeScrollText.Blank, "empty native metadata has visible-safe fallbacks");
string veryLong = string.Concat(Enumerable.Repeat("很长的歌词🙂", 20));
var parts = NativeScrollText.SplitPrimary(veryLong);
Check(parts.Count > 1 && string.Concat(parts) == veryLong, "oversized lyrics are segmented without dropping characters");
Check(parts.All(part => Encoding.UTF8.GetByteCount(part) <= NativeScrollText.MaximumPrimaryBytes),
    "every oversized-lyric segment leaves room for the secondary field");
Check(parts.All(part => !part.Contains('\ufffd')), "splitting does not corrupt UTF-16 surrogate pairs");
nativeFrame = NativeScrollText.Fit(new string('长', 39), "下一句");
Check(nativeFrame.First.Length == 39 && nativeFrame.Second == NativeScrollText.Blank,
    "a long current lyric keeps priority over its preview");
nativeFrame = NativeScrollText.Fit(new string('a', 100), new string('b', 100));
Check(Encoding.UTF8.GetByteCount(nativeFrame.First + nativeFrame.Second) <= NativeScrollText.MaximumBytes,
    "ASCII metadata respects transport bytes without a CJK character-count cap");
parts = NativeScrollText.SplitPrimary(new string('a', 100));
Check(parts.Count == 1 && parts[0] == new string('a', 100), "100 ASCII letters fit the primary transport budget without a 39-character cut");
string english = "This lyric has more than twenty letters and spaces";
Check(NativeScrollText.SplitPrimary(english, 60).SequenceEqual(new[] { english }), "English is not cut at 20 characters");
string englishLong = string.Join(" ", Enumerable.Repeat("singing across the open sky", 6));
parts = NativeScrollText.SplitPrimary(englishLong, 60);
Check(string.Concat(parts) == englishLong && parts.All(p => Encoding.UTF8.GetByteCount(p) <= 60), "word-boundary segmentation preserves English and spaces");
Check(parts.Take(parts.Count - 1).All(p => p.EndsWith(' ') || p.StartsWith(' ')), "English segments prefer word boundaries");
double MeasurePreview(string value) => value.EnumerateRunes().Sum(r => r.Value == 'i' ? 0.25 : r.Value == 'W' ? 1.2 : r.Value == ' ' ? 0.3 : r.Value == '…' ? 1 : r.Value >= 0x3000 ? 2 : 0.7);
string narrow = StaticPreviewText.Fit(new string('i', 20), MeasurePreview, 10);
string wide = StaticPreviewText.Fit(new string('W', 20), MeasurePreview, 10);
Check(narrow == new string('i', 20) && wide.EndsWith('…'), "static preview distinguishes narrow and wide English glyphs");
foreach (string sample in new[] { "中文英文Mixed WWW iii 下一句很长的歌词", "one two three four five six seven", "中文歌词包含标点和空格 继续", "🙂🙂🙂🙂🙂🙂🙂🙂" })
{
    string preview = StaticPreviewText.Fit(sample, MeasurePreview, 12);
    Check(MeasurePreview(preview) <= 12 && Encoding.UTF8.GetByteCount(preview) <= 60,
        "static mixed preview reserves ellipsis width and fits its transport budget");
}
Check(StaticPreviewText.Fit("   ", MeasurePreview, 12) == NativeScrollText.Blank, "empty static preview uses the blank fallback");
nativeFrame = NativeScrollText.Fit(new string('a', 117), "I");
Check(nativeFrame.First.Length == 117 && nativeFrame.Second == "I", "a short English preview is retained beside a full transport segment");
Console.WriteLine($"PASS {checks} playback, codec and native text checks");
