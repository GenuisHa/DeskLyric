using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace DeskLyric;

internal static class Program
{
	private sealed class TrayContext : ApplicationContext
	{
		protected override void Dispose(bool disposing)
		{
			if (disposing && _tray != null)
			{
				_engine?.Dispose();
				_supervisor?.Dispose();
				_tray.Visible = false;
				_tray.Dispose();
			}
			base.Dispose(disposing);
		}
	}

	internal static readonly string Version = typeof(Program).Assembly.GetName().Version!.ToString(3);
	internal static readonly string BaseDir = AppContext.BaseDirectory;

	internal static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");

	internal static readonly string LogPath = Path.Combine(BaseDir, "desklyric.log");

	private static NotifyIcon? _tray;

	private static ToolStripMenuItem? _autoItem;

	private static ToolStripMenuItem? _bootItem;

	private static ToolStripMenuItem? _toggleItem;
	private static ToolStripMenuItem? _nativeItem;
	private static ToolStripMenuItem? _pagedItem;
	private static ToolStripMenuItem? _secondaryMenu;
	private static ToolStripMenuItem? _nextLyricItem;
	private static ToolStripMenuItem? _songInfoItem;
	private static ToolStripMenuItem? _staticNextItem;

	private static Config _config = new Config();

	private static LyricEngine? _engine;

	private static Timer? _supervisor;

	private static bool _lastRunning;

	private static bool _lastEngine;
	private static bool _exiting;

	private static string StartupLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DeskLyricStandalone.lnk");

	[STAThread]
	private static void Main(string[] args)
	{
		AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => Log("unhandled: " + eventArgs.ExceptionObject);
		if (args.Contains("--diagnose"))
		{
			Diagnostics.Run(args).GetAwaiter().GetResult();
			return;
		}
		using Mutex instance = new(true, "Local\\DeskLyric.Standalone", out bool created);
		if (!created) return;
		Log($"DeskLyric {Version}; source=netease-elog");
		ApplicationConfiguration.Initialize();
		_config = LoadConfig();
		Log($"config loaded autoFollow={_config.AutoFollow} nativeScrolling={_config.NativeScrolling} showNextLyric={_config.ShowNextLyric}");
		_engine = new LyricEngine(_config);
		BuildTray();
		Log("tray built");
		_supervisor = new Timer
		{
			Interval = 800
		};
		_supervisor.Tick += delegate
		{
			_supervisor.Interval = 1000;
			Supervise();
		};
		_supervisor.Start();
		System.Windows.Forms.Timer? runLimit = null;
		string? limit = args.FirstOrDefault(value => value.StartsWith("--run-for="));
		if (limit != null && int.TryParse(limit[10..], out int seconds) && seconds > 0)
		{
			runLimit = new() { Interval = Math.Clamp(seconds, 1, 3600) * 1000 };
			runLimit.Tick += (_, _) => ExitApp();
			runLimit.Start();
		}
		Log("entering message loop");
		Application.Run(new TrayContext());
		runLimit?.Dispose();
	}

	private static void Supervise()
	{
		if (_exiting) return;
		bool flag = IsNeteaseRunning();
		if (_config.AutoFollow)
		{
			if (flag)
			{
				_engine?.Start();
			}
			else
			{
				_ = _engine?.StopAsync();
			}
		}
		else if (!flag)
		{
			LyricEngine? engine = _engine;
			if (engine != null && engine.IsRunning)
			{
				_ = engine.StopAsync();
			}
		}
		if (flag != _lastRunning || (_engine?.IsRunning ?? false) != _lastEngine)
		{
			_lastRunning = flag;
			_lastEngine = _engine?.IsRunning ?? false;
			Log($"state: netease={flag} engine={_lastEngine} auto={_config.AutoFollow}");
		}
		RefreshMenu(flag);
	}

	private static bool IsNeteaseRunning()
	{
		try
		{
			Process[] processes = Process.GetProcessesByName("cloudmusic");
			bool running = processes.Length != 0;
			foreach (Process process in processes) process.Dispose();
			return running;
		}
		catch
		{
			return false;
		}
	}

	private static void BuildTray()
	{
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		_autoItem = new ToolStripMenuItem("自动跟随网易云", null, delegate
		{
			ToggleAuto();
		})
		{
			Checked = _config.AutoFollow
		};
		_bootItem = new ToolStripMenuItem("开机自启动", null, delegate
		{
			ToggleBoot();
		})
		{
			Checked = _config.AutoStartWithWindows
		};
		_toggleItem = new ToolStripMenuItem("立即开始推送", null, delegate
		{
			ToggleManual();
		});
		contextMenuStrip.Items.Add(_autoItem);
		contextMenuStrip.Items.Add(_bootItem);
		ToolStripMenuItem displayMenu = new("歌词显示");
		_nativeItem = new("平滑滚动（设备原生）", null, (_, _) => SetScrolling(true));
		_pagedItem = new("静态分页", null, (_, _) => SetScrolling(false));
		displayMenu.DropDownItems.Add(_nativeItem);
		displayMenu.DropDownItems.Add(_pagedItem);
		contextMenuStrip.Items.Add(displayMenu);
		_secondaryMenu = new("第二行内容");
		_nextLyricItem = new("下一句歌词（双行滚动）", null, (_, _) => SetSecondaryContent(true));
		_staticNextItem = new("下一句歌词（静态预告）", null, (_, _) => SetSecondaryLayout(true, true));
		_songInfoItem = new("歌名和歌手", null, (_, _) => SetSecondaryContent(false));
		_secondaryMenu.DropDownItems.Add(_nextLyricItem);
		_secondaryMenu.DropDownItems.Add(_staticNextItem);
		_secondaryMenu.DropDownItems.Add(_songInfoItem);
		contextMenuStrip.Items.Add(_secondaryMenu);
		contextMenuStrip.Items.Add(new ToolStripSeparator());
		contextMenuStrip.Items.Add(_toggleItem);
		contextMenuStrip.Items.Add(new ToolStripMenuItem("打开日志", null, delegate
		{
			OpenLog();
		}));
		contextMenuStrip.Items.Add(new ToolStripMenuItem("打开所在文件夹", null, delegate
		{
			OpenFolder();
		}));
		contextMenuStrip.Items.Add(new ToolStripSeparator());
		contextMenuStrip.Items.Add(new ToolStripMenuItem("退出", null, delegate
		{
			ExitApp();
		}));
		_tray = new NotifyIcon
		{
			Icon = SystemIcons.Application,
			Text = "DeskLyric 免插件版",
			Visible = true,
			ContextMenuStrip = contextMenuStrip
		};
		_tray.DoubleClick += delegate
		{
			ToggleManual();
		};
		RefreshMenu(IsNeteaseRunning());
	}

	private static void RefreshMenu(bool neteaseRunning)
	{
		if (_tray != null)
		{
			bool flag = _engine?.IsRunning ?? false;
			string status = flag ? _engine!.Status : (neteaseRunning ? "已停止推送" : "网易云未运行");
			string tooltip = "DeskLyric 免插件版 · " + status;
			_tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
			if (_toggleItem != null)
			{
				_toggleItem.Text = (flag ? "立即停止推送" : "立即开始推送");
			}
			if (_autoItem != null)
			{
				_autoItem.Checked = _config.AutoFollow;
			}
			if (_bootItem != null)
			{
				_bootItem.Checked = _config.AutoStartWithWindows;
			}
			if (_nativeItem != null) _nativeItem.Checked = _config.NativeScrolling;
			if (_pagedItem != null) _pagedItem.Checked = !_config.NativeScrolling;
			if (_secondaryMenu != null) _secondaryMenu.Enabled = _config.NativeScrolling;
			if (_nextLyricItem != null) _nextLyricItem.Checked = _config.ShowNextLyric && !_config.StaticNextLyric;
			if (_staticNextItem != null) _staticNextItem.Checked = _config.ShowNextLyric && _config.StaticNextLyric;
			if (_songInfoItem != null) _songInfoItem.Checked = !_config.ShowNextLyric;
		}
	}

	private static void SetScrolling(bool native)
	{
		if (_exiting) return;
		_config.NativeScrolling = native;
		Log("display mode changed: " + (native ? "native scrolling" : "static paging"));
		SaveConfig();
		_engine?.RefreshDisplay(refreshOutput: true);
		RefreshMenu(IsNeteaseRunning());
	}

	private static void SetSecondaryContent(bool nextLyric)
		=> SetSecondaryLayout(nextLyric, false);

	private static void SetSecondaryLayout(bool nextLyric, bool staticPreview)
	{
		if (_exiting) return;
		_config.ShowNextLyric = nextLyric;
		_config.StaticNextLyric = staticPreview;
		SaveConfig();
		_engine?.RefreshDisplay();
		RefreshMenu(IsNeteaseRunning());
	}

	private static void ToggleAuto()
	{
		if (_exiting) return;
		_config.AutoFollow = !_config.AutoFollow;
		SaveConfig();
		if (!_config.AutoFollow)
		{
			_ = _engine?.StopAsync();
		}
		Supervise();
	}

	private static void ToggleManual()
	{
		if (_exiting) return;
		LyricEngine? engine = _engine;
		if (engine != null && engine.IsRunning)
		{
			_config.AutoFollow = false;
			SaveConfig();
			_ = engine.StopAsync();
		}
		else
		{
			_engine?.Start();
		}
		RefreshMenu(IsNeteaseRunning());
	}

	private static void ToggleBoot()
	{
		_config.AutoStartWithWindows = !_config.AutoStartWithWindows;
		SaveConfig();
		try
		{
			if (_config.AutoStartWithWindows)
			{
				CreateStartupShortcut();
			}
			else
			{
				RemoveStartupShortcut();
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("设置开机启动失败：" + ex.Message, "DeskLyric");
			_config.AutoStartWithWindows = !_config.AutoStartWithWindows;
			SaveConfig();
		}
		RefreshMenu(IsNeteaseRunning());
	}

	private static void CreateStartupShortcut()
	{
		object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 不可用"))
			?? throw new InvalidOperationException("无法创建启动快捷方式");
		dynamic val2 = ((dynamic)shell).CreateShortcut(StartupLnk);
		val2.TargetPath = Application.ExecutablePath;
		val2.WorkingDirectory = BaseDir;
		val2.Description = "DeskLyric 网易云歌词推送";
		val2.Save();
		Log("startup shortcut created");
	}

	private static void RemoveStartupShortcut()
	{
		if (File.Exists(StartupLnk))
		{
			File.Delete(StartupLnk);
		}
		Log("startup shortcut removed");
	}

	private static void OpenLog()
	{
		try
		{
			if (!File.Exists(LogPath))
			{
				File.WriteAllText(LogPath, "");
			}
			Process.Start(new ProcessStartInfo(LogPath)
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	private static void OpenFolder()
	{
		try
		{
			Process.Start(new ProcessStartInfo(BaseDir)
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	private static async void ExitApp()
	{
		if (_exiting) return;
		_exiting = true;
		_supervisor?.Stop();
		if (_tray?.ContextMenuStrip != null) _tray.ContextMenuStrip.Enabled = false;
		if (_engine != null) await _engine.StopAsync();
		if (_tray != null)
		{
			_tray.Visible = false;
		}
		Log("exit");
		Application.Exit();
	}

	internal static Config LoadConfig()
	{
		try
		{
			if (File.Exists(ConfigPath))
			{
				return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
			}
		}
		catch
		{
		}
		return new Config();
	}

	internal static void SaveConfig()
	{
		try
		{
			string contents = JsonSerializer.Serialize(_config, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(ConfigPath, contents);
		}
		catch
		{
		}
	}

	internal static void Log(string msg)
	{
		string contents = $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}";
		try
		{
			File.AppendAllText(LogPath, contents);
		}
		catch
		{
		}
	}
}
