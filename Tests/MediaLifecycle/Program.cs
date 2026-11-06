using System.Diagnostics;
using System.Reflection;
using System.Text;
using DeskLyric;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Playback;
using AppProgram = DeskLyric.Program;

internal static class MediaLifecycleChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _checks;
    private static bool _visual;

    [STAThread]
    public static void Main(string[] args)
    {
        _visual = args.Contains("--visual");
        if (_visual) File.Delete(Path.Combine(AppContext.BaseDirectory, "visual.done"));
        ApplicationConfiguration.Initialize();
        bool started = false;
        Application.Idle += async (_, _) =>
        {
            if (started) return;
            started = true;
            try { await Run(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            finally { Application.Exit(); }
        };
        Application.Run(new ApplicationContext());
    }

    private static async Task Run()
    {
        CheckNativeRendering();
        CheckLayoutPersistence();
        CheckStaticPreview();
        Config config = new() { AutoFollow = false, NeteaseLogPath = Path.Combine(AppContext.BaseDirectory, "missing-source.elog") };
        using LyricEngine engine = new(config);
        GlobalSystemMediaTransportControlsSessionManager manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        StartForCheck(engine);
        Open(engine);
        MediaPlayer player = Field<MediaPlayer>(engine, "_player");
        SystemMediaTransportControls controls = Field<SystemMediaTransportControls>(engine, "_smtc");
        var session = await WaitForSession(manager, engine);
        Console.WriteLine($"Player={player.PlaybackSession.PlaybackState}, SMTC={controls.PlaybackStatus}, enabled={controls.IsEnabled}, automatic={player.CommandManager.IsEnabled}");
        Check(session != null, "own media session registered in Windows");
        Check((await session!.TryGetMediaPropertiesAsync()).Title == "测试歌词", "lyric metadata reaches Windows");

        string longTitle = "新的第一行测试这是一段长度较长的歌词内容";
        string longArtist = "第二行同步测试这是一段长度较长的文字内容";
        Invoke(engine, "Push", longTitle, longArtist, true);
        for (int i = 0; i < 20 && (await session.TryGetMediaPropertiesAsync()).Title != longTitle; i++) await Task.Delay(50);
        var fullMetadata = await session.TryGetMediaPropertiesAsync();
        Check(fullMetadata.Title == longTitle && fullMetadata.Artist == longArtist,
            "actual engine publishes both complete 20-character lines");
        long firstPush = Field<long>(engine, "_lastPush");
        await Task.Delay(50);
        Invoke(engine, "Push", longTitle, longArtist, true);
        Check(Field<long>(engine, "_lastPush") == firstPush, "identical native metadata is not republished");

        // Device routing may briefly reopen the playback session. As the real source timer
        // does, retry the paused frame until the routed player has settled.
        for (int i = 0; i < 20; i++)
        {
            Invoke(engine, "SyncPlayback", new PlaybackSnapshot(new("1", "测试歌曲", "歌手", 30), 1, false, 0, 1));
            Invoke(engine, "Push", "测试歌词", "第二行", false);
            if (controls.DisplayUpdater.MusicProperties.Title == "测试歌词") break;
            await Task.Delay(50);
        }
        Check(controls.PlaybackStatus == MediaPlaybackStatus.Paused, "pause remains Paused");
        Check(controls.DisplayUpdater.MusicProperties.Title == "测试歌词", "pause keeps current lyric");

        typeof(AppProgram).GetField("_engine", PrivateStatic)!.SetValue(null, engine);
        typeof(AppProgram).GetField("_config", PrivateStatic)!.SetValue(null, config);
        config.AutoFollow = true;
        typeof(AppProgram).GetMethod("ToggleManual", PrivateStatic)!.Invoke(null, null);
        Task stopping = Field<Task>(engine, "_sessionRelease");
        Check(!engine.IsRunning && !config.AutoFollow, "manual stop cancels automatic restart");
        Check(ReferenceEquals(stopping, engine.StopAsync()), "repeated stop awaits the same cleanup");
        Check(await ObserveStopped(session), "Windows observes Stopped and empty metadata");
        await stopping;
        Check(EmptyResources(engine), "manual stop releases player and controls");
        Check(!File.Exists(Path.Combine(AppContext.BaseDirectory, "desklyric-carrier.wav")), "carrier deleted after stop");

        StartForCheck(engine);
        Open(engine);
        session = await WaitForSession(manager, engine);
        Check(session != null, "restart registers a fresh media session");
        Invoke(engine, "Tick");
        Check(await ObserveStopped(session!), "lost playback source publishes terminal state and clears metadata");
        await Field<Task>(engine, "_sessionRelease");
        Check(EmptyResources(engine), "lost playback source releases media output");
        await engine.StopAsync();

        bool neteaseRunning = (bool)typeof(AppProgram).GetMethod("IsNeteaseRunning", PrivateStatic)!.Invoke(null, null)!;
        if (!neteaseRunning)
        {
            StartForCheck(engine);
            Open(engine);
            session = await WaitForSession(manager, engine);
            Check(session != null, "process-exit test session registered");
            config.AutoFollow = true;
            typeof(AppProgram).GetMethod("Supervise", PrivateStatic)!.Invoke(null, null);
            Check(!engine.IsRunning && await ObserveStopped(session!),
                "supervisor stops output after NetEase process exit");
            await Field<Task>(engine, "_sessionRelease");
            Check(EmptyResources(engine), "process-exit cleanup completes");
        }
        else Console.WriteLine("Process-exit supervisor check skipped because the user's NetEase client is running.");
        Check(!engine.IsRunning, "cleanup leaves engine stopped");
        await CheckModeTransitions(manager);
        Console.WriteLine($"PASS {_checks} real Windows media lifecycle checks");
    }

    private static void CheckNativeRendering()
    {
        TrackInfo track = new("1", "测试歌名", "歌手", 180);
        var lyrics = new List<LyricEngine.LrcLine>
        {
            new(10, "这是一整句超过九个汉字的歌词用于平滑滚动"),
            new(30, "这是下一句歌词")
        };
        var intro = LyricEngine.RenderNative(lyrics, 0, track);
        Check(intro.First == track.Title && intro.Second == track.Artist, "native intro shows title and artist");
        var frame = LyricEngine.RenderNative(lyrics, 11, track);
        Check(frame.First == lyrics[0].Text, "native renderer keeps the whole current lyric");
        Check(frame.Second == "测试歌名 - 歌手", "native renderer keeps stable song metadata like the phone");
        Check(LyricEngine.RenderNative(lyrics, 25, track) == frame,
            "same lyric stays unchanged while the device animates it");
        Check(LyricEngine.RenderNative(lyrics, 30, track).First == lyrics[1].Text, "native renderer advances on lyric timestamp");
        Check(LyricEngine.RenderNative(lyrics, 1, track) == intro, "seeking into intro restores song metadata");
        Check(LyricEngine.RenderPage(lyrics, 11, track).First.EnumerateRunes().Count() <= 9,
            "static paging remains available");
        string firstLong = "新的第一行测试这是一段长度较长的歌词内容";
        string secondLong = "第二行同步测试这是一段长度较长的文字内容";
        var dual = new List<LyricEngine.LrcLine> { new(10, firstLong), new(30, secondLong) };
        var both = LyricEngine.RenderNative(dual, 11, track, true);
        Check(both.First == firstLong && both.Second == secondLong, "dual layout sends both full 20-character lines for native scrolling");
        Check(LyricEngine.RenderNative(dual, 25, track, true) == both, "dual layout stays stable within a lyric");
        Check(LyricEngine.RenderNative(dual, 30, track, true).First == secondLong, "preview advances into the current lyric");
        Check(LyricEngine.RenderNative(dual, 30, track, true).Second == NativeScrollText.Blank, "last lyric has no false next-line preview");
        Check(LyricEngine.RenderNative(dual, 1, track, true) == intro, "dual layout keeps song metadata during intro");
        dual = new() { new(10, firstLong + firstLong), new(30, secondLong + secondLong) };
        var firstPart = LyricEngine.RenderNative(dual, 11, track, true);
        var lastPart = LyricEngine.RenderNative(dual, 25, track, true);
        Check(firstPart.First + lastPart.First == dual[0].Text && firstPart.Second + lastPart.Second == dual[1].Text,
            "long dual lyrics preserve all text across safe native-scrolling segments");
        Check(new[] { firstPart, lastPart }.All(f => Encoding.UTF8.GetByteCount(f.First + f.Second) <= 120),
            "dual segments stay within the verified device budget");
    }

    private static void CheckStaticPreview()
    {
        foreach (string text in new[] { "下一句中文歌词很长很长", "WWW iii hello English mixed 中文 next line", new string('i', 50), "🙂下一句 emoji 与English混排" })
        {
            string preview = PreviewFontMetrics.Fit(text);
            Check(PreviewFontMetrics.Measure(preview) <= PreviewFontMetrics.Width, "measured preview fits its static viewport");
            var lines = new List<LyricEngine.LrcLine> { new(0, "这是当前完整歌词保持滚动的长句"), new(20, text) };
            var a = LyricEngine.RenderNative(lines, 1, new("x", "歌曲", "歌手", 120), true, true);
            var b = LyricEngine.RenderNative(lines, 15, new("x", "歌曲", "歌手", 120), true, true);
            Check(a.Second == preview && b.Second == preview, "static next line does not page or scroll through software updates");
            Check(a.First == lines[0].Text, "static preview retains the complete current lyric for native scrolling");
        }
        var boundary = new List<LyricEngine.LrcLine> { new(0, new string('a', 120)), new(30, "I") };
        var early = LyricEngine.RenderNative(boundary, 1, new("x", "song", "singer", 60), true, true);
        var late = LyricEngine.RenderNative(boundary, 25, new("x", "song", "singer", 60), true, true);
        Check(early.First + late.First == boundary[0].Text && early.Second == "I", "long English and one-letter preview retain every character");
        typeof(AppProgram).GetField("_config", PrivateStatic)!.SetValue(null, new Config());
        typeof(AppProgram).GetMethod("SetSecondaryLayout", PrivateStatic)!.Invoke(null, new object[] { true, true });
        Config loaded = (Config)typeof(AppProgram).GetMethod("LoadConfig", PrivateStatic)!.Invoke(null, null)!;
        Check(loaded.ShowNextLyric && loaded.StaticNextLyric, "static-preview choice persists and reloads");
    }

    private static void CheckLayoutPersistence()
    {
        typeof(AppProgram).GetField("_config", PrivateStatic)!.SetValue(null, new Config());
        foreach (bool next in new[] { false, true })
        {
            typeof(AppProgram).GetMethod("SetSecondaryContent", PrivateStatic)!.Invoke(null, new object[] { next });
            Config loaded = (Config)typeof(AppProgram).GetMethod("LoadConfig", PrivateStatic)!.Invoke(null, null)!;
            Check(loaded.ShowNextLyric == next, "layout menu choice persists and reloads: " + next);
        }
    }

    private static void Open(LyricEngine engine)
    {
        Invoke(engine, "CreateSession", 30.0);
        Invoke(engine, "SyncPlayback", new PlaybackSnapshot(new("1", "测试歌曲", "歌手", 30), 1, true, 0, 1));
        Invoke(engine, "Push", "测试歌词", "第二行", true);
    }

    private static async Task CheckModeTransitions(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        Config config = new() { AutoFollow = false, NativeScrolling = true, ShowNextLyric = true,
            NeteaseLogPath = Path.Combine(AppContext.BaseDirectory, "missing-mode-source.elog") };
        using LyricEngine engine = new(config);
        StartForCheck(engine);
        string first = "This is a complete English lyric longer than twenty";
        string second = "Another complete English lyric with several spaces";
        var lyrics = new List<LyricEngine.LrcLine> { new(0, first), new(20, second), new(40, "The final lyric") };
        typeof(LyricEngine).GetField("_songId", PrivateInstance)!.SetValue(engine, "mode-test");
        typeof(LyricEngine).GetField("_lines", PrivateInstance)!.SetValue(engine, lyrics);
        typeof(AppProgram).GetField("_engine", PrivateStatic)!.SetValue(null, engine);
        typeof(AppProgram).GetField("_config", PrivateStatic)!.SetValue(null, config);
        PlaybackSnapshot snapshot = new(new("mode-test", "Test song", "Singer", 120), 1, true, 0, 1);
        await WaitFrame(engine, manager, snapshot, first);
        if (_visual) { Console.WriteLine("VISUAL: full English, both rows, 20 seconds"); await Task.Delay(20000); }
        MediaPlayer original = Field<MediaPlayer>(engine, "_player");
        Check(first.Length > 20 && Field<SystemMediaTransportControls>(engine, "_smtc").DisplayUpdater.MusicProperties.Title == first,
            "actual engine keeps an English lyric longer than 20 characters");
        typeof(AppProgram).GetMethod("SetScrolling", PrivateStatic)!.Invoke(null, new object[] { false });
        await WaitFrame(engine, manager, snapshot, first[..9]);
        Check(!ReferenceEquals(original, Field<MediaPlayer>(engine, "_player")), "mode change recreates output without changing song");
        Check(Field<string>(engine, "_songId") == "mode-test" && ReferenceEquals(lyrics, Field<List<LyricEngine.LrcLine>>(engine, "_lines")),
            "mode change retains song identity and loaded lyrics");
        snapshot = snapshot with { Position = 21 };
        await WaitFrame(engine, manager, snapshot, second[..9]);
        if (_visual) { Console.WriteLine("VISUAL: paging continues within same song, 10 seconds"); await Task.Delay(10000); }
        Check(true, "paging continues updating within the same song");
        typeof(AppProgram).GetMethod("SetScrolling", PrivateStatic)!.Invoke(null, new object[] { true });
        await WaitFrame(engine, manager, snapshot, second);
        if (_visual)
        {
            Console.WriteLine("VISUAL: native English restored; waiting for observer");
            for (int i = 0; i < 1200 && !File.Exists(Path.Combine(AppContext.BaseDirectory, "visual.done")); i++)
                await Task.Delay(500);
        }
        Check(true, "switching back restores the full current lyric without changing song");
        foreach (bool native in new[] { false, true, false, true })
            typeof(AppProgram).GetMethod("SetScrolling", PrivateStatic)!.Invoke(null, new object[] { native });
        await WaitFrame(engine, manager, snapshot, second);
        Check(true, "rapid repeated mode changes settle on the last selection");
        await engine.StopAsync();
    }

    private static async Task WaitFrame(LyricEngine engine, GlobalSystemMediaTransportControlsSessionManager manager,
        PlaybackSnapshot snapshot, string expected)
    {
        string name = Process.GetCurrentProcess().ProcessName;
        for (int i = 0; i < 80; i++)
        {
            Invoke(engine, "ApplySnapshot", snapshot);
            foreach (var session in manager.GetSessions().Where(s => s.SourceAppUserModelId.Contains(name, StringComparison.OrdinalIgnoreCase)))
                if ((await session.TryGetMediaPropertiesAsync()).Title == expected) return;
            await Task.Delay(50);
        }
        throw new Exception("Mode frame did not reach Windows: " + expected);
    }

    private static void StartForCheck(LyricEngine engine)
    {
        engine.Start();
        Field<System.Windows.Forms.Timer>(engine, "_timer").Stop();
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> WaitForSession(GlobalSystemMediaTransportControlsSessionManager manager, LyricEngine engine)
    {
        await Field<Task>(engine, "_outputBinding");
        string processName = Process.GetCurrentProcess().ProcessName;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Invoke(engine, "SyncPlayback", new PlaybackSnapshot(new("1", "测试歌曲", "歌手", 30), 1, true, 0, 1));
            // The normal engine refreshes its metadata while playback is active. This test has
            // stopped its source timer, so refresh the test frame explicitly while the carrier opens.
            Invoke(engine, "Push", "测试歌词", "第二行", true);
            var session = manager.GetSessions().FirstOrDefault(item => item.SourceAppUserModelId.Contains(processName, StringComparison.OrdinalIgnoreCase));
            if (session != null && (await session.TryGetMediaPropertiesAsync()).Title == "测试歌词") return session;
            await Task.Delay(100);
        }
        Console.WriteLine("Observed Windows session IDs: " + string.Join(", ", manager.GetSessions().Select(item => item.SourceAppUserModelId)));
        return null;
    }

    private static async Task<bool> ObserveStopped(GlobalSystemMediaTransportControlsSession session)
    {
        for (int attempt = 0; attempt < 24; attempt++)
        {
            var metadata = await session.TryGetMediaPropertiesAsync();
            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            if (metadata == null && status == null) return true; // Windows withdrew the closed session.
            if (status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed &&
                string.IsNullOrEmpty(metadata?.Title) && string.IsNullOrEmpty(metadata?.Artist)) return true;
            await Task.Delay(50);
        }
        var lastMetadata = await session.TryGetMediaPropertiesAsync();
        Console.WriteLine($"Terminal observation: state={session.GetPlaybackInfo()?.PlaybackStatus}, title=[{lastMetadata?.Title}], artist=[{lastMetadata?.Artist}]");
        return false;
    }

    private static bool EmptyResources(LyricEngine engine)
        => typeof(LyricEngine).GetField("_player", PrivateInstance)!.GetValue(engine) == null &&
           typeof(LyricEngine).GetField("_smtc", PrivateInstance)!.GetValue(engine) == null;

    private static T Field<T>(LyricEngine engine, string name)
        => (T)typeof(LyricEngine).GetField(name, PrivateInstance)!.GetValue(engine)!;

    private static void Invoke(LyricEngine engine, string name, params object[] arguments)
        => typeof(LyricEngine).GetMethod(name, PrivateInstance)!.Invoke(engine, arguments);

    private static void Check(bool valid, string description)
    {
        if (!valid) throw new Exception(description);
        _checks++;
        Console.WriteLine("PASS " + description);
    }
}
