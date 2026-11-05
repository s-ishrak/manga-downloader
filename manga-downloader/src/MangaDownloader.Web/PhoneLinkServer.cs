using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;

namespace MangaDownloader.Web
{
    // A separate, opt-in listener exposes only the link submission page, never the desktop API.
    internal sealed class PhoneLinkServer : IDisposable
    {
        public const int Port = 8781;
        public int BoundPort { get; private set; } = Port;
        private IWebHost host;
        private readonly string token;
        public string[] Urls { get; }
        public bool[][][] QrCodes { get; private set; }
        public string Token => token;

        public PhoneLinkServer(Func<string, object> enqueue, string token = null)
        {
            this.token = string.IsNullOrEmpty(token) ? LocalWebServer.NewToken() : token;

            IWebHost BuildHost(int listenPort) =>
                new WebHostBuilder().UseKestrel(options =>
                {
                    options.Listen(IPAddress.Any, listenPort);
                    options.Limits.MaxRequestBodySize = 16384;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                }).Configure(app => app.Run(async context =>
                {
                    var req = context.Request;
                    var res = context.Response;
                    res.Headers["Cache-Control"] = "no-store";
                    res.Headers["Referrer-Policy"] = "no-referrer";
                    res.Headers["X-Content-Type-Options"] = "nosniff";
                    res.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; form-action 'none'";

                    if (!IPAddress.TryParse(req.Host.Host, out var address) || !IsLocalAddress(address))
                    {
                        res.StatusCode = 403;
                        return;
                    }

                    // The token is carried in the URL fragment and sent in a header by the phone page.
                    if (req.Method == "GET" && req.Path == "/")
                    {
                        res.ContentType = "text/html; charset=utf-8";
                        using var stream = typeof(PhoneLinkServer).Assembly.GetManifestResourceStream("phone.html");
                        await stream.CopyToAsync(res.Body);
                        return;
                    }

                    if (req.Method != "POST" || req.Path != "/api/queue")
                    {
                        res.StatusCode = 404;
                        return;
                    }

                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Headers["X-Token"].ToString()), Encoding.UTF8.GetBytes(this.token)))
                    {
                        res.StatusCode = 401;
                        return;
                    }

                    if (!string.Equals(req.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase) ||
                        (req.Headers.ContainsKey("Origin") && req.Headers["Origin"] != $"http://{req.Host}"))
                    {
                        res.StatusCode = 403;
                        return;
                    }

                    res.ContentType = "application/json; charset=utf-8";
                    try
                    {
                        using var reader = new StreamReader(req.Body);
                        var result = enqueue(await reader.ReadToEndAsync());
                        await res.WriteAsync(JsonSerializer.Serialize(result));
                    }
                    catch (ArgumentException e)
                    {
                        res.StatusCode = 400;
                        await res.WriteAsync(JsonSerializer.Serialize(new { error = e.Message }));
                    }
                    catch (JsonException)
                    {
                        res.StatusCode = 400;
                        await res.WriteAsync("{\"error\":\"Invalid JSON.\"}");
                    }
                })).Build();

            try
            {
                try
                {
                    host = BuildHost(Port);
                    host.Start();
                    BoundPort = Port;
                }
                catch (Exception e) when (e is IOException || e is SocketException || e.InnerException is SocketException)
                {
                    host?.Dispose();
                    host = BuildHost(0);
                    host.Start();
                }

                var bound = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
                BoundPort = new Uri(bound).Port;
                Urls = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Distinct().Select(a => $"http://{a}:{BoundPort}/#token={this.token}").ToArray();
                using var generator = new QRCodeGenerator();
                QrCodes = Urls.Select(url =>
                {
                    using var qr = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                    return qr.ModuleMatrix.Select(row => row.Cast<bool>().ToArray()).ToArray();
                }).ToArray();
            }
            catch
            {
                host?.Dispose();
                throw;
            }
        }

        private static bool IsLocalAddress(IPAddress address) => IPAddress.IsLoopback(address) ||
            NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(address));

        public void Dispose() => host?.Dispose();
    }
}
