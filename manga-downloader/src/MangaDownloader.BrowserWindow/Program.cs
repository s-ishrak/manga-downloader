using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MangaDownloader.BrowserWindow
{
    /// <summary>
    /// A popup browser for passing a Cloudflare check by hand. It shows an ordinary Chromium (WebView2) window with the site in it and does
    /// nothing to the page: no scripts, no clicks, no automation hooks. The only thing it does is look at the browser's cookie jar (from the
    /// outside, invisible to the page) so it can close itself and hand the cookies and user agent back once you are through.
    ///
    /// Exit codes: 0 = cookies written to --out (or folder picked), 2 = window closed before the check was passed, 3 = timed out, 4 = WebView2 runtime missing/failed.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 1 && string.Equals(args[0], "--pick-folder", StringComparison.OrdinalIgnoreCase))
            {
                ApplicationConfiguration();
                using var dialog = new FolderBrowserDialog
                {
                    AutoUpgradeEnabled = true,
                    UseDescriptionForTitle = true,
                    Description = "Select output folder"
                };

                if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]) && Directory.Exists(args[1]))
                {
                    dialog.SelectedPath = args[1];
                }

                if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    Console.Out.Write(dialog.SelectedPath);
                }

                return 0;
            }

            var options = Options.Parse(args);

            if (options == null)
            {
                Console.Error.WriteLine("usage: manga-downloader-browser --url URL --profile DIR (--out FILE [--timeout SECONDS] [--hidden] | --standalone)");
                return 4;
            }

            ApplicationConfiguration();
            var form = new BrowserForm(options);
            Application.Run(form);
            return form.ExitCode;
        }

        private static void ApplicationConfiguration()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
    }

    internal sealed class Options
    {
        public string Url;
        public string Profile;
        public string Out;
        public int TimeoutSeconds = 300;
        public bool Hidden;
        public bool Standalone;

        public static Options Parse(string[] args)
        {
            var o = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--url" when i + 1 < args.Length: o.Url = args[++i]; break;
                    case "--profile" when i + 1 < args.Length: o.Profile = args[++i]; break;
                    case "--out" when i + 1 < args.Length: o.Out = args[++i]; break;
                    case "--timeout" when i + 1 < args.Length: o.TimeoutSeconds = int.Parse(args[++i]); break;
                    case "--hidden": o.Hidden = true; break;
                    case "--standalone": o.Standalone = true; break;
                }
            }

            var validUrl = Uri.TryCreate(o.Url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
            return validUrl && o.Profile != null && (o.Standalone || o.Out != null) ? o : null;
        }
    }

    internal sealed class WindowBoundsState
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public FormWindowState WindowState { get; set; } = FormWindowState.Normal;
    }

    internal sealed class BrowserForm : Form
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private readonly Options options;
        private readonly Uri target;
        private readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        private readonly Label hint = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button done = new Button { Text = "I'm through — continue", Dock = DockStyle.Right, Width = 190 };
        private readonly Timer poll = new Timer { Interval = 1000 };
        private readonly DateTime deadline;

        private List<CoreWebView2Cookie> latest = new List<CoreWebView2Cookie>();
        private string latestUserAgent;
        private int throughPolls;
        private bool finished;
        private bool polling;

        public int ExitCode { get; private set; } = 2;

        public BrowserForm(Options options)
        {
            this.options = options;
            target = new Uri(options.Url);
            deadline = DateTime.UtcNow.AddSeconds(options.TimeoutSeconds);

            Text = options.Standalone ? "Manga Downloader" : "Cloudflare check - " + target.Host;
            Width = 1000;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(20, 22, 26);

            try
            {
                Icon = AppIcon.Create(64);
            }
            catch
            {
                // Fall back to default application icon if programmatic icon generation is unavailable
            }

            web.DefaultBackgroundColor = Color.FromArgb(255, 20, 22, 26);
            Controls.Add(web);

            if (!options.Standalone)
            {
                var bar = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 44,
                    BackColor = Color.FromArgb(24, 27, 32),
                    Padding = new Padding(12, 6, 12, 6)
                };
                bar.Paint += (s, pe) =>
                {
                    using var borderPen = new Pen(Color.FromArgb(45, 52, 64), 1f);
                    pe.Graphics.DrawLine(borderPen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
                };

                hint.Text = "Complete the check below. This window closes by itself when you are through (or click the button).";
                hint.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
                hint.ForeColor = Color.FromArgb(226, 232, 240);
                hint.Padding = new Padding(8, 0, 8, 0);

                done.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                done.BackColor = Color.FromArgb(79, 70, 229);
                done.ForeColor = Color.White;
                done.FlatStyle = FlatStyle.Flat;
                done.FlatAppearance.BorderSize = 0;
                done.FlatAppearance.MouseOverBackColor = Color.FromArgb(67, 56, 202);
                done.Cursor = Cursors.Hand;
                done.Width = 190;

                bar.Controls.Add(hint);
                bar.Controls.Add(done);
                Controls.Add(bar);
                bar.BringToFront();
            }
            else
            {
                LoadWindowState();
            }

            if (options.Hidden)
            {
                // For automated tests only: keep the window out of sight.
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
            }

            done.Click += async (s, e) => await Finish(fromButton: true);
            poll.Tick += async (s, e) => await Poll();
            FormClosing += OnClosing;
            Load += async (s, e) => await Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (IsDarkModeEnabled())
            {
                ApplyDarkTitlebar();
            }
        }

        private static bool IsDarkModeEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                if (val is int light)
                    return light == 0;
            }
            catch { }
            return true;
        }

        private void ApplyDarkTitlebar()
        {
            if (!OperatingSystem.IsWindows() || !IsHandleCreated) return;
            try
            {
                int useDarkMode = 1;
                if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
                }
            }
            catch { }
        }

        private void LoadWindowState()
        {
            if (!options.Standalone) return;
            try
            {
                var stateFile = Path.Combine(options.Profile, "window-state.json");
                if (!File.Exists(stateFile)) return;
                var json = File.ReadAllText(stateFile);
                var state = JsonSerializer.Deserialize<WindowBoundsState>(json);
                if (state == null || state.Width < 200 || state.Height < 200) return;

                var targetBounds = new Rectangle(state.Left, state.Top, state.Width, state.Height);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(targetBounds)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = targetBounds;
                    if (state.WindowState == FormWindowState.Maximized)
                    {
                        WindowState = FormWindowState.Maximized;
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Could not restore window state: " + e.Message);
            }
        }

        private void SaveWindowState()
        {
            if (!options.Standalone) return;
            try
            {
                Directory.CreateDirectory(options.Profile);
                var stateFile = Path.Combine(options.Profile, "window-state.json");
                var boundsToSave = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                var state = new WindowBoundsState
                {
                    Left = boundsToSave.Left,
                    Top = boundsToSave.Top,
                    Width = boundsToSave.Width,
                    Height = boundsToSave.Height,
                    WindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState
                };
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(stateFile, json);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Could not save window state: " + e.Message);
            }
        }

        private async Task Start()
        {
            try
            {
                Directory.CreateDirectory(options.Profile);
                var env = await CoreWebView2Environment.CreateAsync(null, options.Profile);
                await web.EnsureCoreWebView2Async(env);
                web.CoreWebView2.Navigate(target.AbsoluteUri);

                if (!options.Standalone)
                    poll.Start();
            }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException || e is System.Runtime.InteropServices.COMException || e is InvalidOperationException)
            {
                Console.Error.WriteLine("WebView2 could not start: " + e.Message);
                ExitCode = 4;
                finished = true;
                Close();
            }
        }

        private static bool LooksLikeChallenge(string title) =>
            string.IsNullOrEmpty(title) ||
            title.IndexOf("just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("attention required", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("verify you are human", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("security check", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Looks at the cookie jar and the page title (browser-side properties; the page cannot see this) once a second.</summary>
        private async Task Poll()
        {
            if (finished || polling || web.CoreWebView2 == null)
                return;

            polling = true;

            try
            {
                if (DateTime.UtcNow > deadline)
                {
                    ExitCode = 3;
                    finished = true;
                    Close();
                    return;
                }

                latest = (await web.CoreWebView2.CookieManager.GetCookiesAsync(target.GetLeftPart(UriPartial.Authority) + "/")).ToList();
                latestUserAgent = web.CoreWebView2.Settings.UserAgent;

                var onSite = Uri.TryCreate(web.CoreWebView2.Source, UriKind.Absolute, out var src) && src.Host.EndsWith(target.Host, StringComparison.OrdinalIgnoreCase);
                var through = onSite && latest.Any(c => c.Name == "cf_clearance") && !LooksLikeChallenge(web.CoreWebView2.DocumentTitle);

                throughPolls = through ? throughPolls + 1 : 0;

                if (throughPolls >= 2)
                    await Finish(fromButton: false);
            }
            finally
            {
                polling = false;
            }
        }

        private async Task Finish(bool fromButton)
        {
            if (finished)
                return;

            try
            {
                latest = (await web.CoreWebView2.CookieManager.GetCookiesAsync(target.GetLeftPart(UriPartial.Authority) + "/")).ToList();
                latestUserAgent = web.CoreWebView2.Settings.UserAgent;
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.Runtime.InteropServices.COMException)
            {
                // use what the last poll saw
            }

            if (latest.Count == 0)
            {
                if (fromButton)
                    MessageBox.Show(this, "No cookies were set yet. Finish the check in the page first.", Text);

                return;
            }

            Write();
            finished = true;
            ExitCode = 0;
            Close();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            poll.Stop();

            // Closing the window yourself counts as "I'm through" when the browser already holds a clearance cookie.
            if (!finished && latest.Any(c => c.Name == "cf_clearance"))
            {
                Write();
                finished = true;
                ExitCode = 0;
            }

            SaveWindowState();
        }

        private void Write()
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("userAgent", latestUserAgent);
                    w.WriteString("url", web.CoreWebView2?.Source ?? target.AbsoluteUri);
                    w.WriteStartArray("cookies");

                    foreach (var c in latest)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", c.Name);
                        w.WriteString("value", c.Value);
                        w.WriteString("domain", c.Domain);
                        w.WriteString("path", c.Path);
                        // epoch seconds; -1 for session cookies
                        w.WriteNumber("expires", c.IsSession ? -1 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds());
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                File.WriteAllBytes(options.Out, stream.ToArray());
            }
        }
    }

    internal static class AppIcon
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create(int size = 64)
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
