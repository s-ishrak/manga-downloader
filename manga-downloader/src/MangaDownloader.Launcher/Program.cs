using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
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
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static readonly object LogGate = new object();

        private readonly NotifyIcon trayIcon;
        private readonly Icon appIcon;
        private readonly EventWaitHandle openEvent;
        private readonly RegisteredWaitHandle openWait;
        private readonly string logFile;
        private readonly string appDirectory;
        private readonly string uiProfileDirectory;
        private readonly Control dispatcher;
        private LauncherSettings settings;
        private Process server;
        private Process browser;
        private string webUiUrl;
        private bool useDefaultBrowser;
        private bool exiting;
        private int syncingModules;

        public LauncherContext()
        {
            dispatcher = new Control();
            _ = dispatcher.Handle;
            appDirectory = AppContext.BaseDirectory;
            logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "manga-downloader", "launcher.log");
            uiProfileDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "manga-downloader", "webview-ui-profile");

            try
            {
                appIcon = AppIcon.Create(32);
            }
            catch
            {
                appIcon = SystemIcons.Application;
            }

            trayIcon = new NotifyIcon
            {
                Icon = appIcon,
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
                try
                {
                    settings = LoadSettings(Path.Combine(appDirectory, "launcher.json"));
                    var marker = Path.Combine(Path.GetDirectoryName(logFile) ?? "", "modules", "lua", ".setup-complete");
                    if (string.IsNullOrWhiteSpace(settings.Modules) && !File.Exists(marker))
                    {
                        using var setup = new SetupWindow(OpenLog, appIcon);
                        setup.Show();
                        while (!exiting)
                        {
                            setup.Working();
                            try { await Task.Run(() => StartServer(appDirectory)); break; }
                            catch (Exception e)
                            {
                                AppendLog(e.ToString());
                                if (!await setup.Failed(e.Message)) { ExitLauncher(); return; }
                            }
                        }
                        setup.Close();
                    }
                    else await Task.Run(() => StartServer(appDirectory));
                }
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
            menu.Items.Add("Sync site modules", null, (_, __) => SyncModulesInBackground());
            menu.Items.Add("Open log", null, (_, __) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => ExitLauncher());
            return menu;
        }

        private void RotateLogIfNeeded()
        {
            try
            {
                if (File.Exists(logFile))
                {
                    var fi = new FileInfo(logFile);
                    if (fi.Length > 2 * 1024 * 1024)
                    {
                        var dir = Path.GetDirectoryName(logFile) ?? "";
                        var oldLog = Path.Combine(dir, "launcher.old.log");
                        File.Copy(logFile, oldLog, overwrite: true);
                        File.WriteAllText(logFile, string.Empty);
                    }
                }
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            if (line == null)
                return;

            lock (LogGate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(logFile);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    RotateLogIfNeeded();
                    File.AppendAllText(logFile, line + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private string GetResolvedModulesDirectory()
        {
            var mod = settings?.Modules;
            if (!string.IsNullOrWhiteSpace(mod))
            {
                return Path.GetFullPath(Path.Combine(appDirectory, Environment.ExpandEnvironmentVariables(mod)));
            }
            return Path.Combine(Path.GetDirectoryName(logFile) ?? "", "modules", "lua");
        }

        private void SyncModulesInBackground()
        {
            if (Interlocked.CompareExchange(ref syncingModules, 1, 0) != 0)
            {
                trayIcon.ShowBalloonTip(2500, "Manga Downloader", "Module sync is already in progress.", ToolTipIcon.Info);
                return;
            }

            trayIcon.ShowBalloonTip(2500, "Manga Downloader", "Syncing site modules…", ToolTipIcon.Info);

            Task.Run(() =>
            {
                try
                {
                    var executable = Path.Combine(appDirectory, "manga-downloader.exe");
                    if (!File.Exists(executable))
                    {
                        trayIcon.ShowBalloonTip(3000, "Manga Downloader", "Could not find manga-downloader.exe.", ToolTipIcon.Error);
                        return;
                    }

                    var modulesDir = GetResolvedModulesDirectory();
                    AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting background module sync with modules path: {modulesDir}");

                    var syncInfo = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    syncInfo.ArgumentList.Add("modules");
                    syncInfo.ArgumentList.Add("sync");
                    syncInfo.ArgumentList.Add("--modules");
                    syncInfo.ArgumentList.Add(modulesDir);

                    using var syncProc = Process.Start(syncInfo);
                    if (syncProc == null)
                    {
                        trayIcon.ShowBalloonTip(3000, "Manga Downloader", "Could not start module sync process.", ToolTipIcon.Error);
                        return;
                    }

                    var stdout = syncProc.StandardOutput.ReadToEndAsync();
                    var stderr = syncProc.StandardError.ReadToEndAsync();

                    if (!syncProc.WaitForExit(300000))
                    {
                        syncProc.Kill(true);
                        AppendLog("Module sync timed out after 5 minutes.");
                        trayIcon.ShowBalloonTip(4000, "Manga Downloader", "Module sync timed out.", ToolTipIcon.Warning);
                        return;
                    }

                    var outText = stdout.GetAwaiter().GetResult();
                    var errText = stderr.GetAwaiter().GetResult();

                    if (!string.IsNullOrWhiteSpace(outText)) AppendLog(outText.Trim());
                    if (!string.IsNullOrWhiteSpace(errText)) AppendLog(errText.Trim());

                    if (syncProc.ExitCode == 0)
                    {
                        trayIcon.ShowBalloonTip(3000, "Manga Downloader", "Site modules updated successfully.", ToolTipIcon.Info);
                    }
                    else
                    {
                        trayIcon.ShowBalloonTip(4000, "Manga Downloader", "Module sync failed. Check launcher.log for details.", ToolTipIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("Module sync failed: " + ex.Message);
                    trayIcon.ShowBalloonTip(4000, "Manga Downloader", "Module sync error: " + ex.Message, ToolTipIcon.Error);
                }
                finally
                {
                    Interlocked.Exchange(ref syncingModules, 0);
                }
            });
        }

        private void StartServer(string appDirectory)
        {
            var executable = Path.Combine(appDirectory, "manga-downloader.exe");
            var settingsFile = Path.Combine(appDirectory, "launcher.json");

            if (!File.Exists(executable))
                throw new FileNotFoundException("manga-downloader.exe must be next to the launcher.", executable);

            settings = LoadSettings(settingsFile);

            AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting Manga Downloader");

            if (string.IsNullOrWhiteSpace(settings.Modules))
            {
                settings.Modules = Path.Combine(Path.GetDirectoryName(logFile) ?? "", "modules", "lua");
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
                if (!File.Exists(file))
                    return new LauncherSettings();

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

        private void OpenWebUi()
        {
            if (dispatcher.InvokeRequired)
            {
                dispatcher.BeginInvoke(new Action(OpenWebUi));
                return;
            }

            if (exiting)
                return;

            if (useDefaultBrowser)
            {
                OpenInDefaultBrowser();
                return;
            }

            if (browser != null)
            {
                try
                {
                    if (!browser.HasExited && browser.MainWindowHandle != IntPtr.Zero)
                    {
                        SetForegroundWindow(browser.MainWindowHandle);
                        return;
                    }
                }
                catch { }
            }

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
                    browser = Process.Start(info);
                    var opened = browser;
                    browser.ErrorDataReceived += (_, e) => AppendLog(e.Data);
                    browser.Exited += (_, __) =>
                    {
                        // Exit code 4 means WebView2 could not initialize. Keep the app usable by
                        // opening its local page in the user's normal browser instead.
                        var exitCode = opened.ExitCode;
                        if (exiting)
                        {
                            opened.Dispose();
                            return;
                        }
                        try
                        {
                            dispatcher.BeginInvoke(new Action(() =>
                            {
                                browser = null;
                                opened.Dispose();
                                if (exiting)
                                    return;

                                if (exitCode == 4)
                                {
                                    useDefaultBrowser = true;
                                    AppendLog("The embedded app window could not start; opening the web UI in the default browser.");
                                    OpenInDefaultBrowser();
                                }
                                else if (settings == null || !settings.CloseToTray)
                                {
                                    ExitLauncher();
                                }
                                // When CloseToTray is true, closing the window leaves the server and tray icon running.
                            }));
                        }
                        catch (InvalidOperationException) { opened.Dispose(); }
                    };
                    browser.BeginErrorReadLine();
                    browser.EnableRaisingEvents = true;
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
                if (browser != null && !browser.HasExited)
                    browser.Kill(true);
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
            appIcon?.Dispose();
            dispatcher.Dispose();
            server?.Dispose();
            base.ExitThreadCore();
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    internal sealed class SetupWindow : Form
    {
        private readonly Label title;
        private readonly Label status;
        private readonly ProgressBar progress;
        private readonly Button retry;
        private readonly Button cancel;
        private readonly Button log;
        private TaskCompletionSource<bool> decision;
        private bool allowClose;

        public SetupWindow(Action openLog, Icon icon = null)
        {
            Text = "Manga Downloader Setup";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.White;

            if (icon != null)
            {
                Icon = icon;
            }

            title = new Label
            {
                Left = 24,
                Top = 20,
                Width = 452,
                Height = 26,
                Text = "Setting up Manga Downloader",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(17, 24, 39)
            };

            status = new Label
            {
                Left = 24,
                Top = 50,
                Width = 452,
                Height = 44,
                Text = "Downloading site modules. This may take a few minutes depending on your internet connection.",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(71, 85, 105)
            };

            progress = new ProgressBar
            {
                Left = 24,
                Top = 104,
                Width = 452,
                Height = 10,
                Style = ProgressBarStyle.Marquee
            };

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                BackColor = Color.FromArgb(248, 250, 252)
            };
            bottomPanel.Paint += (s, pe) =>
            {
                using var linePen = new Pen(Color.FromArgb(226, 232, 240), 1f);
                pe.Graphics.DrawLine(linePen, 0, 0, bottomPanel.Width, 0);
            };

            retry = new Button
            {
                Left = 24,
                Top = 11,
                Width = 90,
                Height = 32,
                Text = "Retry",
                Enabled = false,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            retry.FlatAppearance.BorderSize = 0;

            cancel = new Button
            {
                Left = 124,
                Top = 11,
                Width = 90,
                Height = 32,
                Text = "Exit",
                Enabled = false,
                Font = new Font("Segoe UI", 9.5f),
                FlatStyle = FlatStyle.System
            };

            log = new Button
            {
                Left = 386,
                Top = 11,
                Width = 90,
                Height = 32,
                Text = "Open log",
                Font = new Font("Segoe UI", 9.5f),
                FlatStyle = FlatStyle.System
            };

            retry.Click += (_, __) => decision?.TrySetResult(true);
            cancel.Click += (_, __) => decision?.TrySetResult(false);
            log.Click += (_, __) => openLog();
            FormClosing += (_, e) =>
            {
                if (!allowClose)
                {
                    e.Cancel = true;
                    decision?.TrySetResult(false);
                }
            };

            bottomPanel.Controls.Add(retry);
            bottomPanel.Controls.Add(cancel);
            bottomPanel.Controls.Add(log);

            Controls.AddRange(new Control[] { title, status, progress, bottomPanel });
        }

        public void Working()
        {
            title.Text = "Setting up Manga Downloader";
            status.Text = "Downloading site modules. This may take a few minutes depending on your internet connection.";
            status.ForeColor = Color.FromArgb(71, 85, 105);
            progress.Style = ProgressBarStyle.Marquee;
            retry.Enabled = cancel.Enabled = false;
        }

        public Task<bool> Failed(string message)
        {
            title.Text = "Setup Encountered an Issue";
            status.Text = message;
            status.ForeColor = Color.FromArgb(220, 38, 38);
            progress.Style = ProgressBarStyle.Blocks;
            retry.Enabled = cancel.Enabled = true;
            decision = new TaskCompletionSource<bool>();
            return decision.Task;
        }

        protected override void Dispose(bool disposing)
        {
            allowClose = true;
            base.Dispose(disposing);
        }

        public new void Close()
        {
            allowClose = true;
            base.Close();
        }
    }

    internal sealed class LauncherSettings
    {
        public string Modules { get; set; }
        public int Port { get; set; } = 8780;
        public bool BrowserChallenges { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
    }

    internal static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create(int size = 32)
        {
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float s = size;
                // Indigo rounded rectangle background (#4F46E5)
                float pad = s * 0.05f;
                var bgRect = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
                float radius = s * 0.22f;
                using var bgPath = CreateRoundedRect(bgRect, radius);
                using var bgBrush = new SolidBrush(Color.FromArgb(79, 70, 229));
                g.FillPath(bgBrush, bgPath);

                using var whiteBrush = new SolidBrush(Color.FromArgb(248, 250, 252));

                // Crisp downward arrow
                var arrow = new PointF[]
                {
                    new PointF(s * 0.43f, s * 0.16f),
                    new PointF(s * 0.57f, s * 0.16f),
                    new PointF(s * 0.57f, s * 0.35f),
                    new PointF(s * 0.70f, s * 0.35f),
                    new PointF(s * 0.50f, s * 0.53f),
                    new PointF(s * 0.30f, s * 0.35f),
                    new PointF(s * 0.43f, s * 0.35f)
                };
                g.FillPolygon(whiteBrush, arrow);

                // Open book / manga pages
                var leftPage = new PointF[]
                {
                    new PointF(s * 0.20f, s * 0.60f),
                    new PointF(s * 0.46f, s * 0.65f),
                    new PointF(s * 0.46f, s * 0.81f),
                    new PointF(s * 0.20f, s * 0.76f)
                };
                g.FillPolygon(whiteBrush, leftPage);

                var rightPage = new PointF[]
                {
                    new PointF(s * 0.54f, s * 0.65f),
                    new PointF(s * 0.80f, s * 0.60f),
                    new PointF(s * 0.80f, s * 0.76f),
                    new PointF(s * 0.54f, s * 0.81f)
                };
                g.FillPolygon(whiteBrush, rightPage);
            }

            var hIcon = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
