using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MangaDownloader.Launcher
{
    internal static class Program
    {
        private const string MutexName = "Local\\MangaDownloader.Launcher.5E340622";
        private const string OpenEventName = "Local\\MangaDownloader.Launcher.Open.5E340622";

        [STAThread]
        private static void Main()
        {
            using var mutex = new Mutex(true, MutexName, out var firstInstance);

            if (!firstInstance)
            {
                try
                {
                    using var openEvent = EventWaitHandle.OpenExisting(OpenEventName);
                    openEvent.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    MessageBox.Show("Manga Downloader is already running. Use its tray icon to open it.", "Manga Downloader",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new LauncherContext());
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Manga Downloader could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class LauncherContext : ApplicationContext
    {
        private readonly NotifyIcon trayIcon;
        private readonly EventWaitHandle openEvent;
        private readonly RegisteredWaitHandle openWait;
        private readonly string logFile;
        private readonly string appDirectory;
        private readonly string uiProfileDirectory;
        private Process server;
        private string webUiUrl;
        private bool exiting;

        public LauncherContext()
        {
            appDirectory = AppContext.BaseDirectory;
            logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "manga-downloader", "launcher.log");
            uiProfileDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "manga-downloader", "webview-ui-profile");

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Manga Downloader is starting…",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            trayIcon.DoubleClick += (_, __) => OpenWebUi();

            openEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                "Local\\MangaDownloader.Launcher.Open.5E340622");
            openWait = ThreadPool.RegisterWaitForSingleObject(openEvent, (_, __) => OpenWebUi(), null,
                Timeout.Infinite, false);

            var startup = new System.Windows.Forms.Timer { Interval = 100 };
            startup.Tick += async (_, __) =>
            {
                startup.Stop();
                startup.Dispose();
                try { await Task.Run(() => StartServer(appDirectory)); }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "Manga Downloader could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ExitLauncher();
                }
            };
            startup.Start();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Manga Downloader", null, (_, __) => OpenWebUi());
            menu.Items.Add("Open log", null, (_, __) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => ExitLauncher());
            return menu;
        }

        private void StartServer(string appDirectory)
        {
            var executable = Path.Combine(appDirectory, "manga-downloader.exe");
            var settingsFile = Path.Combine(appDirectory, "launcher.json");

            if (!File.Exists(executable))
                throw new FileNotFoundException("manga-downloader.exe must be next to the launcher.", executable);

            var settings = LoadSettings(settingsFile);

            Directory.CreateDirectory(Path.GetDirectoryName(logFile));
            File.AppendAllText(logFile, $"{Environment.NewLine}[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting Manga Downloader{Environment.NewLine}");

            if (string.IsNullOrWhiteSpace(settings.Modules))
            {
                settings.Modules = Path.Combine(Path.GetDirectoryName(logFile), "modules", "lua");
                var marker = Path.Combine(settings.Modules, ".setup-complete");
                if (!File.Exists(marker))
                {
                    trayIcon.Text = "Manga Downloader: downloading modules…";
                    var syncInfo = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    foreach (var argument in new[] { "modules", "sync", "--modules", settings.Modules })
                        syncInfo.ArgumentList.Add(argument);
                    using var sync = Process.Start(syncInfo);
                    var stdout = sync.StandardOutput.ReadToEndAsync();
                    var stderr = sync.StandardError.ReadToEndAsync();
                    if (!sync.WaitForExit(300000))
                    {
                        sync.Kill(true);
                        throw new IOException("Module setup timed out. Check your internet connection and start Manga Downloader again.");
                    }
                    AppendLog(stdout.GetAwaiter().GetResult());
                    AppendLog(stderr.GetAwaiter().GetResult());
                    if (sync.ExitCode != 0)
                        throw new IOException("Module setup failed. Check your internet connection and launcher.log in %APPDATA%\\manga-downloader, then try again.");
                    File.WriteAllText(marker, "Modules initialized successfully.");
                }
            }
            else
            {
                settings.Modules = Path.GetFullPath(Path.Combine(appDirectory, Environment.ExpandEnvironmentVariables(settings.Modules)));
            }
            if (!Directory.Exists(settings.Modules))
                throw new DirectoryNotFoundException("The Lua modules folder in launcher.json was not found:\n" + settings.Modules);
            if (exiting) return;

            var arguments = new StringBuilder("serve");
            arguments.Append(" --modules ").Append(Quote(settings.Modules));
            arguments.Append(" --port ").Append(settings.Port > 0 ? settings.Port : 8780);
            if (settings.BrowserChallenges)
                arguments.Append(" --browser");

            server = new Process
            {
                StartInfo = new ProcessStartInfo(executable, arguments.ToString())
                {
                    WorkingDirectory = Path.GetDirectoryName(logFile),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            server.OutputDataReceived += (_, e) => HandleOutput(e.Data);
            server.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            server.Exited += (_, __) => ServerExited();

            if (!server.Start())
                throw new InvalidOperationException("Windows could not start manga-downloader.exe.");

            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
        }

        private static LauncherSettings LoadSettings(string file)
        {
            try
            {
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LauncherSettings();
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                throw new InvalidOperationException("Could not read launcher.json: " + e.Message, e);
            }
        }

        private void HandleOutput(string line)
        {
            AppendLog(line);
            if (line == null)
                return;

            var start = line.IndexOf("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                start = line.IndexOf("http://localhost:", StringComparison.OrdinalIgnoreCase);

            if (start >= 0)
            {
                var firstUrl = webUiUrl == null;
                webUiUrl = line.Substring(start).Trim();
                trayIcon.Text = "Manga Downloader";
                if (firstUrl && !exiting) OpenWebUi();
            }
        }

        private void AppendLog(string line)
        {
            if (line == null)
                return;

            try { File.AppendAllText(logFile, line + Environment.NewLine); }
            catch (IOException) { }
        }

        private void OpenWebUi()
        {
            if (string.IsNullOrWhiteSpace(webUiUrl))
            {
                trayIcon.ShowBalloonTip(2500, "Manga Downloader", "The web UI is still starting.", ToolTipIcon.Info);
                return;
            }

            try
            {
                var helper = Path.Combine(appDirectory, "manga-downloader-browser.exe");
                if (File.Exists(helper))
                {
                    var info = new ProcessStartInfo(helper)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    info.ArgumentList.Add("--standalone");
                    info.ArgumentList.Add("--url");
                    info.ArgumentList.Add(webUiUrl);
                    info.ArgumentList.Add("--profile");
                    info.ArgumentList.Add(uiProfileDirectory);
                    var browser = Process.Start(info);
                    browser.EnableRaisingEvents = true;
                    browser.ErrorDataReceived += (_, e) => AppendLog(e.Data);
                    browser.Exited += (_, __) =>
                    {
                        // Exit code 4 means WebView2 could not initialize. Keep the app usable by
                        // opening its local page in the user's normal browser instead.
                        if (browser.ExitCode == 4 && !exiting)
                        {
                            AppendLog("The embedded app window could not start; opening the web UI in the default browser.");
                            OpenInDefaultBrowser();
                        }

                        browser.Dispose();
                    };
                    browser.BeginErrorReadLine();
                }
                else
                {
                    OpenInDefaultBrowser();
                }
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Could not open Manga Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OpenInDefaultBrowser()
        {
            try { Process.Start(new ProcessStartInfo(webUiUrl) { UseShellExecute = true }); }
            catch (Exception e) { AppendLog("Could not open the default browser: " + e.Message); }
        }

        private void OpenLog()
        {
            try { Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(e.Message, "Could not open the log", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ServerExited()
        {
            if (exiting)
                return;

            trayIcon.Text = "Manga Downloader stopped";
            trayIcon.ShowBalloonTip(5000, "Manga Downloader stopped",
                "The background server exited. Open the tray menu's log for details.", ToolTipIcon.Error);
        }

        private void ExitLauncher()
        {
            exiting = true;
            try
            {
                if (server != null && !server.HasExited)
                    server.Kill(true);
            }
            catch (InvalidOperationException) { }

            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            openWait?.Unregister(null);
            openEvent?.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            server?.Dispose();
            base.ExitThreadCore();
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    internal sealed class LauncherSettings
    {
        public string Modules { get; set; }
        public int Port { get; set; } = 8780;
        public bool BrowserChallenges { get; set; } = true;
    }
}
