using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace StremioXbox.Core
{
    /// <summary>One request to the remote-control server: "POST /press/a", "GET /state", ...</summary>
    public sealed class RemoteRequest
    {
        public RemoteRequest(string method, string path, string query, string body)
        {
            Method = method;
            Path = path;
            Query = query;
            Body = body;
        }

        public string Method { get; }

        /// <summary>Gets the path without the query, lower-case ("/press/a").</summary>
        public string Path { get; }

        public string Query { get; }

        public string Body { get; }

        /// <summary>Gets a query value ("lines" from "?lines=200"), or null.</summary>
        public string Get(string name)
        {
            foreach (var part in (Query ?? string.Empty).Split('&'))
            {
                var pair = part.Split(new[] { '=' }, 2);
                if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A small HTTP server for testing Stremio-XBOX from a PC on the home network (scripts\Xbox-Remote.ps1):
    /// press controller buttons, open pages, read what is on screen and the log. Only in a sideloaded
    /// (Developer Mode) install, never from the Store, and every request needs the key from LocalState\remote.json,
    /// which the PC fetches once through the Device Portal login.
    /// </summary>
    public sealed class RemoteControl
    {
        public const int Port = 8765;
        private const int MaxBody = 256 * 1024;

        private readonly Func<RemoteRequest, Task<string>> _handle;
        private readonly Action<string, string> _log;
        private StreamSocketListener _listener;
        private string _key;

        public RemoteControl(Func<RemoteRequest, Task<string>> handle, Action<string, string> log)
        {
            _handle = handle;
            _log = log;
        }

        /// <summary>Gets a value indicating whether this install may run the server (sideloaded, not from the Store).</summary>
        public static bool Allowed
        {
            get
            {
                try
                {
                    return Package.Current.SignatureKind != PackageSignatureKind.Store;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public async Task StartAsync()
        {
            if (!Allowed || _listener != null)
            {
                return;
            }

            try
            {
                _key = await LoadKeyAsync();
                _listener = new StreamSocketListener();
                _listener.Control.KeepAlive = false;
                _listener.ConnectionReceived += (sender, args) => _ = ServeAsync(args.Socket);
                await _listener.BindServiceNameAsync(Port.ToString());
                _log("info", "Remote control listening on port " + Port + " (Developer Mode install; key in LocalState\\remote.json)");
            }
            catch (Exception ex)
            {
                _listener = null;
                _log("warn", "Remote control could not start: " + ex.Message);
            }
        }

        // The key stays the same across updates (LocalSettings); remote.json is how the PC picks it up.
        private static async Task<string> LoadKeyAsync()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            var key = settings["remoteKey"] as string;
            if (string.IsNullOrEmpty(key) || key.Length < 32)
            {
                key = CryptographicBuffer.EncodeToHexString(CryptographicBuffer.GenerateRandom(24));
                settings["remoteKey"] = key;
            }

            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("remote.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, "{\"port\":" + Port + ",\"key\":\"" + key + "\"}");
            return key;
        }

        private async Task ServeAsync(StreamSocket socket)
        {
            using (socket)
            {
                var status = "200 OK";
                string body;
                try
                {
                    var input = socket.InputStream.AsStreamForRead();
                    var head = await ReadHeadAsync(input);
                    if (head == null)
                    {
                        return;
                    }

                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 1; i < lines.Length; i++)
                    {
                        var colon = lines[i].IndexOf(':');
                        if (colon > 0)
                        {
                            headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                        }
                    }

                    var length = headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out var n) ? Math.Min(n, MaxBody) : 0;
                    var data = await ReadBodyAsync(input, length);
                    var target = first.Length > 1 ? first[1] : "/";
                    var question = target.IndexOf('?');
                    var request = new RemoteRequest(
                        first[0].ToUpperInvariant(),
                        (question >= 0 ? target.Substring(0, question) : target).ToLowerInvariant(),
                        question >= 0 ? target.Substring(question + 1) : string.Empty,
                        data);

                    if (!headers.TryGetValue("X-Key", out var key) || !SameKey(key, _key))
                    {
                        status = "401 Unauthorized";
                        body = "{\"error\":\"wrong or missing X-Key\"}";
                    }
                    else
                    {
                        body = await _handle(request) ?? "{}";
                    }
                }
                catch (Exception ex)
                {
                    status = "500 Internal Server Error";
                    body = "{\"error\":" + Json(ex.Message) + "}";
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var output = socket.OutputStream.AsStreamForWrite();
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " +
                        bytes.Length + "\r\nConnection: close\r\n\r\n");
                    await output.WriteAsync(header, 0, header.Length);
                    await output.WriteAsync(bytes, 0, bytes.Length);
                    await output.FlushAsync();
                }
                catch (Exception)
                {
                    // The PC went away; nothing to answer.
                }
            }
        }

        private static async Task<string> ReadHeadAsync(Stream input)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 16384)
            {
                if (await input.ReadAsync(one, 0, 1) <= 0)
                {
                    return null;
                }

                bytes.Add(one[0]);
                var c = bytes.Count;
                if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, c - 4);
                }
            }

            return null;
        }

        private static async Task<string> ReadBodyAsync(Stream input, int length)
        {
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var got = await input.ReadAsync(buffer, read, length - read);
                if (got <= 0)
                {
                    break;
                }

                read += got;
            }

            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        private static bool SameKey(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        /// <summary>A JSON string literal.</summary>
        public static string Json(string text)
        {
            if (text == null)
            {
                return "null";
            }

            var builder = new StringBuilder("\"");
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.AppendFormat("\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
