using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MangaDownloader.Net
{
    public static class NetworkErrors
    {
        /// <summary>A one-line, human explanation of a network failure (the .NET message chain is noisy and has no advice).</summary>
        public static string Describe(Exception e)
        {
            var socket = Find<SocketException>(e);

            if (socket != null)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                    case SocketError.TryAgain:
                    case SocketError.NoData:
                        return "could not resolve the host name (" + Detail(e) + "). The name does not resolve from this computer: check DNS, a network/ISP block, or use --proxy.";
                    case SocketError.ConnectionRefused:
                    case SocketError.TimedOut:
                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                        return "could not connect (" + socket.SocketErrorCode + "): " + Detail(e) + ".";
                }
            }

            if (e is TaskCanceledException)
                return "the request timed out.";

            return Detail(e);
        }

        private static string Detail(Exception e)
        {
            var http = Find<HttpRequestException>(e);
            return (http ?? e).Message;
        }

        private static T Find<T>(Exception e) where T : Exception
        {
            for (; e != null; e = e.InnerException)
                if (e is T match)
                    return match;

            return null;
        }
    }
}
