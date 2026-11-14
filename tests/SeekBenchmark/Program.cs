// Seek benchmark for HttpChunkCache (runs on a Windows PC, no Xbox needed) against a local server that
// behaves like a debrid CDN: range requests, MBPS MB/s per connection (default 25), and a slow first byte
// (COLD_MS, default 1500 ms) when a request starts far from anything served recently, 150 ms otherwise.
// Every byte read is checked. Example: set MBPS=5 and COLD_MS=3000, then dotnet run -c Release
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StremioXbox.Core;

internal static class Program
{
    private const long FileSize = 2L * 1024 * 1024 * 1024; // 2 GB, generated on the fly.
    private static readonly int BytesPerSecond = int.Parse(Environment.GetEnvironmentVariable("MBPS") ?? "25") * 1024 * 1024;
    private static readonly int ColdMs = int.Parse(Environment.GetEnvironmentVariable("COLD_MS") ?? "1500");
    private static readonly List<long> Served = new List<long>();
    private static readonly int Limit = int.Parse(Environment.GetEnvironmentVariable("LIMIT") ?? "0"); // Connections the server allows (0 = any).
    private static int _requests;
    private static int _active;
    private static int _refused;

    private static byte At(long p) => (byte)((p * 31) ^ (p >> 11));

    private static async Task Main()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => Serve(listener));
        var url = "http://127.0.0.1:" + port + "/file.mkv";

        var log = new List<string>();
        var cache = new HttpChunkCache(url, new Uri(url), FileSize, 6, 256L << 20, (level, text) => { lock (log) { log.Add(level + " " + text); } });

        // Play from the start for a while so all connections are busy with read-ahead.
        await ReadRange(cache, 0, 24 << 20, "start");
        await Task.Delay(1500);

        // Far seek, like +20 min in a remux.
        var results = new List<string>();
        foreach (var target in new[] { 1200L << 20, 1700L << 20 })
        {
            cache.SeekStarted();
            var first = Stopwatch.StartNew();
            var buffer = new Windows.Storage.Streams.Buffer(65536);
            var got = await cache.ReadAsync(target, buffer, 0, 65536, CancellationToken.None, 1);
            var firstMs = first.ElapsedMilliseconds;
            Verify(buffer, target, got);
            var eight = await ReadRange(cache, target + got, (32 << 20) - got, "after seek");
            results.Add(string.Format("seek to {0} MB: first 64 KB after {1} ms, first 32 MB after {2} ms", target >> 20, firstMs, firstMs + eight));
            await Task.Delay(1000);
        }

        // Playing on: read at a video's pace while read-ahead fills the window, as during a film. Debrid CDNs
        // block an address that sends too many requests, so the requests per MB matter.
        var before = _requests;
        var downloaded = cache.DownloadedBytes;
        var playhead = (1700L << 20) + (32 << 20);
        var play = Stopwatch.StartNew();
        while (play.Elapsed < TimeSpan.FromSeconds(20))
        {
            await ReadRange(cache, playhead, 1 << 20, "playing");
            playhead += 1 << 20;
            await Task.Delay(250); // 4 MB/s, a 32 Mbps film.
        }

        var playedMb = (cache.DownloadedBytes - downloaded) / 1048576.0;
        results.Add(string.Format("playing 20 s: {0:0} MB downloaded with {1} requests ({2:0.0} MB per request)",
            playedMb, _requests - before, playedMb / Math.Max(1, _requests - before)));

        // Two cursors, as the Xbox player reads some remuxes: a slow one and the video 150 MB ahead of it.
        cache.SeekStarted(true);
        var slowAt = 600L << 20;
        var fastAt = slowAt + (150L << 20);
        var fastWaits = 0L;
        var fastStalls = 0;
        var two = Stopwatch.StartNew();
        var slowTask = Task.Run(async () =>
        {
            while (two.Elapsed < TimeSpan.FromSeconds(20))
            {
                await ReadRange(cache, slowAt, 64 * 1024, "slow cursor");
                slowAt += 64 * 1024;
                await Task.Delay(500);
            }
        });
        while (two.Elapsed < TimeSpan.FromSeconds(20))
        {
            var took = await ReadRange(cache, fastAt, 1 << 20, "fast cursor");
            if (took > 50)
            {
                fastWaits += took;
                fastStalls++;
            }

            fastAt += 1 << 20;
            await Task.Delay(Math.Max(0, 250 - (int)took)); // 4 MB/s, a 32 Mbps film.
        }

        await slowTask;
        results.Add(string.Format("two cursors 20 s: fast cursor waited {0} times, {1} ms in all, reached {2} MB",
            fastStalls, fastWaits, (fastAt - (750L << 20)) >> 20));

        foreach (var line in results)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine("requests: " + _requests + (Limit > 0 ? ", refused with 429: " + _refused : string.Empty) + ", data verified OK");
        lock (log)
        {
            foreach (var line in log)
            {
                if (line.Contains("Seek:") || line.Contains("failed") || line.StartsWith("error") || line.Contains("too many"))
                {
                    Console.WriteLine("  log: " + line);
                }
            }
        }

        cache.Dispose();
    }

    private static async Task<long> ReadRange(HttpChunkCache cache, long start, long length, string what)
    {
        var watch = Stopwatch.StartNew();
        var position = start;
        var buffer = new Windows.Storage.Streams.Buffer(256 * 1024);
        while (position < start + length)
        {
            var want = (int)Math.Min(256 * 1024, start + length - position);
            var got = await cache.ReadAsync(position, buffer, 0, want, CancellationToken.None, 1);
            if (got <= 0)
            {
                throw new Exception("read returned " + got + " at " + position);
            }

            Verify(buffer, position, got);
            position += got;
        }

        return watch.ElapsedMilliseconds;
    }

    private static void Verify(Windows.Storage.Streams.IBuffer buffer, long position, int count)
    {
        buffer.Length = (uint)count;
        var bytes = buffer.ToArray(0, count);
        for (var i = 0; i < count; i++)
        {
            if (bytes[i] != At(position + i))
            {
                throw new Exception("DATA MISMATCH at " + (position + i));
            }
        }
    }

    private static async Task Serve(TcpListener listener)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => Handle(client));
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                while (true)
                {
                    var requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        return;
                    }

                    long from = 0, to = FileSize - 1;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        {
                            var spec = line.Substring(line.IndexOf('=') + 1).Split('-');
                            from = long.Parse(spec[0]);
                            if (spec[1].Length > 0)
                            {
                                to = long.Parse(spec[1]);
                            }
                        }
                    }

                    Interlocked.Increment(ref _requests);
                    if (Limit > 0 && Interlocked.Increment(ref _active) > Limit)
                    {
                        // Like TorBox's CDN past 20 connections per server: refuse with 429.
                        Interlocked.Decrement(ref _active);
                        Interlocked.Increment(ref _refused);
                        var refusal = Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nRetry-After: 2\r\nContent-Length: 0\r\n\r\n");
                        await stream.WriteAsync(refusal, 0, refusal.Length);
                        continue;
                    }

                    try
                    {
                        await Send(stream, from, to);
                    }
                    finally
                    {
                        if (Limit > 0)
                        {
                            Interlocked.Decrement(ref _active);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Client went away (e.g. a cancelled download).
            }
        }
    }

    private static async Task Send(NetworkStream stream, long from, long to)
    {
        bool cold;
        lock (Served)
        {
            cold = !Served.Exists(p => Math.Abs(p - from) < (64L << 20));
            Served.Add(from);
        }

        await Task.Delay(cold ? ColdMs : 150);
        var header = "HTTP/1.1 206 Partial Content\r\nContent-Type: video/x-matroska\r\nAccept-Ranges: bytes\r\n" +
            "Content-Range: bytes " + from + "-" + to + "/" + FileSize + "\r\nContent-Length: " + (to - from + 1) + "\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        var block = new byte[64 * 1024];
        var watch = Stopwatch.StartNew();
        long sent = 0;
        for (var p = from; p <= to;)
        {
            var n = (int)Math.Min(block.Length, to - p + 1);
            for (var i = 0; i < n; i++)
            {
                block[i] = At(p + i);
            }

            await stream.WriteAsync(block, 0, n);
            p += n;
            sent += n;
            var due = sent * 1000L / BytesPerSecond - watch.ElapsedMilliseconds;
            if (due > 0)
            {
                await Task.Delay((int)due);
            }
        }
    }
}
