using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
    /// Exit codes: 0 = cookies written to --out, 2 = window closed before the check was passed, 3 = timed out, 4 = WebView2 runtime missing/failed.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
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

    internal sealed class BrowserForm : Form
    {
        private readonly Options options;
        private readonly Uri target;
        private readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        private readonly Label hint = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
        private readonly Button done = new Button { Text = "I'm through - continue", Dock = DockStyle.Right, Width = 190 };
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

            Controls.Add(web);

            if (!options.Standalone)
            {
                var bar = new Panel { Dock = DockStyle.Top, Height = 38 };
                hint.Text = "Complete the check below. This window closes by itself when you are through (or click the button).";
                bar.Controls.Add(hint);
                bar.Controls.Add(done);
                Controls.Add(bar);
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
}
