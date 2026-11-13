using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace StremioXbox.Core
{
    /// <summary>What a stream URL resolves to after following redirects.</summary>
    public sealed class StreamProbe
    {
        public Uri FinalUrl { get; set; }

        public long Size { get; set; }

        public string ContentType { get; set; }

        public bool AcceptsRanges { get; set; }
    }

    /// <summary>
    /// Downloads a remote file in fixed-size chunks over several HTTP range connections and keeps a
    /// read-ahead window in RAM. The same idea as segmented download managers (aria2) combined with the
    /// read-ahead cache of players such as mpv and Kodi. The player reads it through <see cref="BufferedHttpStream"/>.
    /// </summary>
    public sealed class HttpChunkCache : IDisposable
    {
        public const int ChunkSize = 4 * 1024 * 1024;

        // MKV keeps its seek index (cues) near the end; the header is in the first chunk. Both stay cached.
        private const int PinnedTailChunks = 8;
        private const int MaxAttempts = 4;

        // A connection that delivers nothing for this long is cut and the chunk retried.
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
        private const int LoggedReads = 40;
        private const int LoggedWaits = 200;

        private readonly object _lock = new object();
        private readonly Dictionary<long, Chunk> _chunks = new Dictionary<long, Chunk>();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, int.MaxValue);
        private readonly SemaphoreSlim _resolving = new SemaphoreSlim(1, 1);
        private readonly HttpClient _client;
        private readonly string _sourceUrl;
        private readonly long _chunkCount;
        private readonly long _aheadChunks;
        private readonly long _behindChunks;
        private readonly int _connections;
        private readonly Action<string, string> _log;
        private Uri _url;
        private DateTime _resolvedAt = DateTime.UtcNow;
        private long _playheadChunk;
        private long _downloadedBytes;
        private long _waitedReads;
        private int _readCount;
        private int _waitLogs;
        private long _useCounter;
        private int _moveLogs;
        private bool _stopStale; // Set by SeekStarted(true); used up by the next read.
        private readonly Queue<KeyValuePair<int, long>> _recentReads = new Queue<KeyValuePair<int, long>>();
        private bool _disposed;

        // Diagnostics (0.1.3.8): which stream view the player reads through, and whether reads overlap.
        private const int LoggedViewEvents = 200;
        private const int LoggedOverlaps = 60;
        private readonly Dictionary<int, ViewStats> _views = new Dictionary<int, ViewStats>();
        private readonly Dictionary<long, KeyValuePair<int, long>> _inFlightReads = new Dictionary<long, KeyValuePair<int, long>>(); // read -> (view, start tick)
        private int _viewCount;
        private int _viewEvents;
        private long _readSeq;
        private int _maxInFlight;
        private int _overlapped;
        private int _behindBlocked;
        private int _overlapLogs;

        public HttpChunkCache(string sourceUrl, Uri resolvedUrl, long size, int connections, long aheadBytes, Action<string, string> log)
        {
            _sourceUrl = sourceUrl;
            _url = resolvedUrl;
            Size = size;
            AheadBytes = aheadBytes;
            _connections = connections;
            _log = log;
            _chunkCount = (size + ChunkSize - 1) / ChunkSize;
            _aheadChunks = Math.Max(2, aheadBytes / ChunkSize);
            _behindChunks = 16; // 64 MB kept behind the playhead for short rewinds.

            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
            filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            filter.MaxConnectionsPerServer = (uint)Math.Max(6, connections + 2);
            _client = new HttpClient(filter);

            for (var i = 0; i < connections; i++)
            {
                Task.Run(() => WorkerAsync(false));
            }

            // One extra connection that only fetches chunks a read is blocked on, so a seek or a
            // missing chunk never queues behind read-ahead downloads.
            Task.Run(() => WorkerAsync(true));
        }

        public long Size { get; }

        public long AheadBytes { get; }

        /// <summary>Gets the total bytes received from the network.</summary>
        public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

        /// <summary>Gets how many player reads had to wait for the network (each one is a potential stall).</summary>
        public long WaitedReads => Interlocked.Read(ref _waitedReads);

        /// <summary>Gets the bytes already in memory, contiguous from the playhead.</summary>
        public long BufferedAheadBytes
        {
            get
            {
                lock (_lock)
                {
                    long bytes = 0;
                    for (var i = _playheadChunk; i < _chunkCount; i++)
                    {
                        if (!_chunks.TryGetValue(i, out var chunk) || chunk.Data == null)
                        {
                            break;
                        }

                        bytes += chunk.Length;
                    }

                    return bytes;
                }
            }
        }

        /// <summary>Gets the number of chunks held in memory.</summary>
        public int ChunksInMemory
        {
            get
            {
                lock (_lock)
                {
                    var count = 0;
                    foreach (var chunk in _chunks.Values)
                    {
                        if (chunk.Data != null)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        /// <summary>
        /// Follows redirects by hand (e.g. AIOStreams -> Comet -> TorBox CDN), logging each hop, and reads
        /// the file size from a 1-byte range request. Returns null if the URL cannot be resolved.
        /// </summary>
        public static async Task<StreamProbe> ResolveAsync(string url, Action<string, string> log)
        {
            try
            {
                var filter = new HttpBaseProtocolFilter { AllowAutoRedirect = false };
                filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
                filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
                using (var client = new HttpClient(filter))
                {
                    var uri = new Uri(url);
                    for (var hop = 0; hop < 6; hop++)
                    {
                        var started = DateTime.UtcNow;
                        using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                        {
                            request.Headers.TryAppendWithoutValidation("Range", "bytes=0-0");
                            var send = client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask();
                            if (await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(15))) != send)
                            {
                                log("warn", "Probe " + hop + ": " + uri.Host + " timed out");
                                return null;
                            }

                            using (var response = await send)
                            {
                                var code = (int)response.StatusCode;
                                log("info", string.Format("Probe {0}: {1} {2} in {3:0} ms", hop, uri.Host, code, (DateTime.UtcNow - started).TotalMilliseconds));
                                if (code >= 300 && code < 400 && response.Headers.Location != null)
                                {
                                    uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                                    continue;
                                }

                                var headers = response.Content.Headers;
                                var size = (long)(headers.ContentRange?.Length ?? (code == 206 ? 0UL : headers.ContentLength ?? 0UL));
                                var probe = new StreamProbe
                                {
                                    FinalUrl = uri,
                                    Size = size,
                                    ContentType = headers.ContentType?.MediaType ?? string.Empty,
                                    AcceptsRanges = code == 206,
                                };
                                log("info", string.Format(
                                    "Probe result: final host {0}, size {1} bytes, type '{2}', ranges {3}, server '{4}'",
                                    uri.Host, size, probe.ContentType, probe.AcceptsRanges ? "yes" : "no", response.Headers.TryGetValue("Server", out var server) ? server : string.Empty));
                                return probe;
                            }
                        }
                    }

                    log("warn", "Probe: too many redirects");
                }
            }
            catch (Exception ex)
            {
                log("warn", "Probe failed: " + ex.GetType().Name + " " + ex.Message);
            }

            return null;
        }

        /// <summary>Copies up to <paramref name="count"/> bytes at <paramref name="position"/> (within one chunk), waiting for the network if needed.</summary>
        /// <returns>The number of bytes copied; 0 at the end of the file.</returns>
        public async Task<int> ReadAsync(long position, IBuffer destination, uint destinationOffset, int count, CancellationToken token, int view = 0)
        {
            if (position >= Size || count <= 0)
            {
                return 0;
            }

            var index = position / ChunkSize;
            var offset = (int)(position - (index * ChunkSize));
            if (Interlocked.Increment(ref _readCount) <= LoggedReads)
            {
                _log("info", string.Format("Read {0}: view {1}, position {2} (chunk {3}), {4} bytes", _readCount, view, position, index, count));
            }

            List<CancellationTokenSource> stale;
            lock (_lock)
            {
                stale = MovePlayheadLocked(index);
            }

            CancelDownloads(stale);

            string state = null;
            var started = DateTime.UtcNow;
            while (true)
            {
                Chunk chunk;
                Task progress;
                lock (_lock)
                {
                    if (!_chunks.TryGetValue(index, out chunk))
                    {
                        chunk = new Chunk(index);
                        _chunks[index] = chunk;
                    }

                    chunk.LastUsed = ++_useCounter;
                    var copied = CopyAvailableLocked(chunk, offset, destination, destinationOffset, count);
                    if (copied > 0)
                    {
                        if (state != null && Interlocked.Increment(ref _waitLogs) <= LoggedWaits)
                        {
                            _log("warn", string.Format(
                                "Read waited {0:0} ms: view {6}, position {1} (chunk {2}, was {3}), playhead chunk {4}, served {5} KB",
                                (DateTime.UtcNow - started).TotalMilliseconds, position, index, state, Interlocked.Read(ref _playheadChunk), copied >> 10, view));
                        }

                        return copied;
                    }

                    if (state == null)
                    {
                        state = chunk.Downloading ? "downloading" : "queued";
                        Interlocked.Increment(ref _waitedReads);
                    }

                    chunk.Waiters++;
                    progress = chunk.Progress.Task;
                    EvictLocked();
                }

                Wake();
                try
                {
                    // Wake on the chunk finishing (or failing) or on more of it arriving: a read needs only
                    // its own bytes, not the whole 4 MB, which after a seek is the difference between the
                    // first video frame and a frozen picture.
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token))
                    {
                        await Task.WhenAny(chunk.Ready.Task, progress, Task.Delay(Timeout.Infinite, linked.Token));
                        linked.Token.ThrowIfCancellationRequested();
                    }

                    if (chunk.Ready.Task.IsFaulted || chunk.Ready.Task.IsCanceled)
                    {
                        await chunk.Ready.Task; // Rethrows a download failure.
                    }
                }
                finally
                {
                    lock (_lock)
                    {
                        chunk.Waiters--;
                    }
                }
            }
        }

        // Copies what is in memory at offset: the finished chunk, or the part of it already downloaded.
        private static int CopyAvailableLocked(Chunk chunk, int offset, IBuffer destination, uint destinationOffset, int count)
        {
            if (chunk.Data != null)
            {
                var length = Math.Min(count, chunk.Length - offset);
                chunk.Data.CopyTo(offset, destination, destinationOffset, length);
                return length;
            }

            if (chunk.Partial != null && chunk.Received > offset)
            {
                var length = Math.Min(count, chunk.Received - offset);
                chunk.Partial.CopyTo(offset, destination, destinationOffset, length);
                return length;
            }

            return 0;
        }

        internal int RegisterView(string origin, ulong position, int parent)
        {
            var id = Interlocked.Increment(ref _viewCount);
            lock (_views)
            {
                _views[id] = new ViewStats();
            }

            LogViewEvent(string.Format("Stream view {0} created by {1} at {2} MB{3}", id, origin, position >> 20, parent > 0 ? " (from view " + parent + ")" : string.Empty));
            return id;
        }

        internal void LogViewEvent(string text)
        {
            if (Interlocked.Increment(ref _viewEvents) <= LoggedViewEvents)
            {
                _log("info", text);
            }
        }

        internal long ReadStarted(int view, long position)
        {
            var now = Environment.TickCount;
            var seq = Interlocked.Increment(ref _readSeq);
            lock (_views)
            {
                if (_inFlightReads.Count > 0)
                {
                    _overlapped++;
                    foreach (var other in _inFlightReads.Values)
                    {
                        var age = unchecked(now - (int)other.Value);
                        if (age > 100)
                        {
                            // A read issued while another has been pending > 100 ms: the player does not
                            // serialize its reads behind the blocked one.
                            _behindBlocked++;
                            if (++_overlapLogs <= LoggedOverlaps)
                            {
                                _log("info", string.Format("Read overlap: view {0} at {1} MB started while view {2} pending {3} ms",
                                    view, position >> 20, other.Key, age));
                            }

                            break;
                        }
                    }
                }

                _inFlightReads[seq] = new KeyValuePair<int, long>(view, now);
                _maxInFlight = Math.Max(_maxInFlight, _inFlightReads.Count);
            }

            return seq;
        }

        internal void ReadFinished(long seq, int view, long position, int bytes, int elapsedMs)
        {
            lock (_views)
            {
                _inFlightReads.Remove(seq);
                if (!_views.TryGetValue(view, out var stats))
                {
                    return;
                }

                stats.Reads++;
                stats.Bytes += bytes;
                stats.MinPosition = Math.Min(stats.MinPosition, position);
                stats.MaxPosition = Math.Max(stats.MaxPosition, position + bytes);
                if (elapsedMs > 20)
                {
                    stats.Waits++;
                    stats.WaitMs += elapsedMs;
                    stats.MaxWaitMs = Math.Max(stats.MaxWaitMs, elapsedMs);

                    // Where the slow reads were, relative to the read-ahead playhead: near it (the video being
                    // played is late) or far ahead (the player buffering ahead at the download edge).
                    var ahead = position - (Interlocked.Read(ref _playheadChunk) * ChunkSize);
                    stats.WaitAheadMin = Math.Min(stats.WaitAheadMin, ahead);
                    stats.WaitAheadMax = Math.Max(stats.WaitAheadMax, ahead);
                }
            }
        }

        /// <summary>Per-view read activity since the last call (then reset), for the 5 s pace log.</summary>
        public string TakeViewSummary()
        {
            var text = new System.Text.StringBuilder("Views:");
            lock (_views)
            {
                foreach (var pair in _views)
                {
                    var stats = pair.Value;
                    if (stats.Reads == 0)
                    {
                        continue;
                    }

                    text.AppendFormat(" v{0} {1} reads {2} MB at {3}-{4} MB, waits {5} ({6} ms total, max {7}{8});",
                        pair.Key, stats.Reads, stats.Bytes >> 20, stats.MinPosition >> 20, stats.MaxPosition >> 20, stats.Waits, stats.WaitMs, stats.MaxWaitMs,
                        stats.Waits > 0 ? string.Format(", at {0}..{1} MB from the playhead", stats.WaitAheadMin >> 20, stats.WaitAheadMax >> 20) : string.Empty);
                    pair.Value.Reset();
                }

                text.AppendFormat(" in flight max {0}, overlapped {1}, behind a pending read {2}", _maxInFlight, _overlapped, _behindBlocked);
                _maxInFlight = _inFlightReads.Count;
                _overlapped = 0;
                _behindBlocked = 0;
            }

            return text.ToString();
        }

        private sealed class ViewStats
        {
            public int Reads;
            public long Bytes;
            public long MinPosition = long.MaxValue;
            public long MaxPosition;
            public int Waits;
            public long WaitMs;
            public int MaxWaitMs;
            public long WaitAheadMin = long.MaxValue;
            public long WaitAheadMax = long.MinValue;

            public void Reset()
            {
                Reads = 0;
                Bytes = 0;
                MinPosition = long.MaxValue;
                MaxPosition = 0;
                Waits = 0;
                WaitMs = 0;
                MaxWaitMs = 0;
                WaitAheadMin = long.MaxValue;
                WaitAheadMax = long.MinValue;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var chunk in _chunks.Values)
                {
                    chunk.Ready.TrySetCanceled();
                }

                _chunks.Clear();
                _pool.Clear();
            }

            _cts.Cancel();
            _client.Dispose();
        }

        // The player reads from two places: playback itself (trailing) and a reader that takes whatever is
        // already buffered (leading, up to the edge of the window). The window is anchored on the trailing
        // one: the lowest chunk read in the last 3 s. 0.1.3.3/0.1.3.4 followed the latest read, so playback's
        // own chunks fell behind the window and were evicted once memory filled up.
        // Reads of the pinned header/tail (container header, MKV cues) do not count.
        /// <summary>
        /// Called by the page before every seek. Forgets recent read positions so the next read sets the
        /// read-ahead window at once; otherwise a jump forward kept downloading the old area for ~3 s
        /// (two quick +60 s jumps froze playback ~15 s in 0.1.3.8).
        /// With <paramref name="stopOldDownloads"/> (a seek the viewer asked for, or the resume point), the
        /// next jump of the window also stops downloads left behind. The page's own short probe seeks pass
        /// false: they jump back a second later, and stopping the downloads there drained the buffer.
        /// </summary>
        public void SeekStarted(bool stopOldDownloads = false)
        {
            lock (_lock)
            {
                _recentReads.Clear();
                _stopStale = stopOldDownloads;
            }
        }

        private List<CancellationTokenSource> MovePlayheadLocked(long index)
        {
            if (index >= _chunkCount - PinnedTailChunks && _chunkCount > PinnedTailChunks + 1)
            {
                return null;
            }

            if (index == 0 && _playheadChunk > 1)
            {
                return null;
            }

            var now = Environment.TickCount;
            _recentReads.Enqueue(new KeyValuePair<int, long>(now, index));
            while (_recentReads.Count > 0 && unchecked(now - _recentReads.Peek().Key) > 3000)
            {
                _recentReads.Dequeue();
            }

            var trailing = index;
            foreach (var read in _recentReads)
            {
                trailing = Math.Min(trailing, read.Value);
            }

            var jumped = Math.Abs(trailing - _playheadChunk) > 4;
            if (jumped && ++_moveLogs <= LoggedWaits)
            {
                _log("info", "Read-ahead window moved: chunk " + _playheadChunk + " -> " + trailing);
            }

            _playheadChunk = trailing;

            // Only the first read after a requested seek may stop downloads: the window also drifts when the
            // player's reads move on their own, and the chunks it would drop then are ones playback needs.
            var stop = jumped && _stopStale;
            _stopStale = false;
            return stop ? TakeStaleDownloadsLocked() : null;
        }

        // After a jump (a seek), downloads still running for the old position would keep every connection
        // busy until they finish, so the new position's chunks queued behind them, each then waiting for
        // the server's first byte in turn. Stop them, so all connections request the new position at once.
        // Chunks a read is waiting for, the pinned header/tail and the first chunks of the new window stay.
        private List<CancellationTokenSource> TakeStaleDownloadsLocked()
        {
            var keepTo = _playheadChunk + Math.Max(4, _connections * 2);
            List<CancellationTokenSource> stale = null;
            List<Chunk> dropped = null;
            foreach (var chunk in _chunks.Values)
            {
                var pinned = chunk.Index == 0 || chunk.Index >= _chunkCount - PinnedTailChunks;
                if (chunk.Downloading && chunk.Cancel != null && chunk.Waiters == 0 && !pinned &&
                    (chunk.Index < _playheadChunk || chunk.Index > keepTo))
                {
                    (stale = stale ?? new List<CancellationTokenSource>()).Add(chunk.Cancel);
                    (dropped = dropped ?? new List<Chunk>()).Add(chunk);
                }
            }

            if (dropped != null)
            {
                // Out of the map now, so a later read starts a fresh download instead of waiting on this one.
                foreach (var chunk in dropped)
                {
                    _chunks.Remove(chunk.Index);
                    chunk.Cancel = null;
                }

                _log("info", "Seek: stopped " + dropped.Count + " download(s) for the old position");
            }

            return stale;
        }

        // Outside the lock: cancelling may run the download's continuation inline.
        private void CancelDownloads(List<CancellationTokenSource> stale)
        {
            if (stale == null)
            {
                return;
            }

            foreach (var cancel in stale)
            {
                try
                {
                    cancel.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            Wake();
        }

        // Keeps chunks outside the read-ahead window while memory allows (0.1.3.3 dropped them at once, so
        // a brief read elsewhere, e.g. the container index, threw away the prefetched data). Over budget,
        // the least recently used chunks outside the window go first.
        private void EvictLocked()
        {
            var budget = _aheadChunks + _behindChunks + PinnedTailChunks + 1 + 32;
            var held = 0;
            foreach (var chunk in _chunks.Values)
            {
                if (chunk.Data != null)
                {
                    held++;
                }
            }

            if (held <= budget)
            {
                return;
            }

            var low = _playheadChunk - _behindChunks;
            var high = _playheadChunk + _aheadChunks;
            var candidates = new List<Chunk>();
            foreach (var chunk in _chunks.Values)
            {
                var pinned = chunk.Index == 0 || chunk.Index >= _chunkCount - PinnedTailChunks;
                if (chunk.Data != null && chunk.Waiters == 0 && !pinned && (chunk.Index < low || chunk.Index > high))
                {
                    candidates.Add(chunk);
                }
            }

            candidates.Sort((x, y) => x.LastUsed.CompareTo(y.LastUsed));
            foreach (var chunk in candidates)
            {
                if (held <= budget)
                {
                    break;
                }

                _pool.Push(chunk.Data);
                chunk.Data = null;
                _chunks.Remove(chunk.Index);
                held--;
            }
        }

        /// <summary>Gets a value indicating whether the byte at <paramref name="position"/> is already in memory.</summary>
        public bool IsAvailable(long position)
        {
            lock (_lock)
            {
                return position < Size && _chunks.TryGetValue(position / ChunkSize, out var chunk) && chunk.Data != null;
            }
        }

        // First: chunks a reader is waiting for (a seek target, the MKV cues). Then: read-ahead from the playhead.
        private Chunk PickNextLocked(bool urgentOnly)
        {
            Chunk best = null;
            foreach (var chunk in _chunks.Values)
            {
                if (chunk.Data == null && !chunk.Downloading && chunk.Waiters > 0 &&
                    (best == null || Math.Abs(chunk.Index - _playheadChunk) < Math.Abs(best.Index - _playheadChunk)))
                {
                    best = chunk;
                }
            }

            if (best != null || urgentOnly)
            {
                return best;
            }

            var last = Math.Min(_chunkCount - 1, _playheadChunk + _aheadChunks);
            for (var i = _playheadChunk; i <= last; i++)
            {
                if (!_chunks.TryGetValue(i, out var chunk))
                {
                    chunk = new Chunk(i);
                    _chunks[i] = chunk;
                    return chunk;
                }

                if (chunk.Data == null && !chunk.Downloading)
                {
                    return chunk;
                }
            }

            return null;
        }

        private void Wake()
        {
            if (_wake.CurrentCount < _connections + 1)
            {
                _wake.Release();
            }
        }

        private async Task WorkerAsync(bool urgentOnly)
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                Chunk chunk;
                lock (_lock)
                {
                    chunk = _disposed ? null : PickNextLocked(urgentOnly);
                    if (chunk != null)
                    {
                        chunk.Downloading = true;
                    }
                }

                if (chunk == null)
                {
                    try
                    {
                        await _wake.WaitAsync(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                await DownloadChunkAsync(chunk, token);
            }
        }

        private async Task DownloadChunkAsync(Chunk chunk, CancellationToken token)
        {
            var start = chunk.Index * ChunkSize;
            var length = (int)Math.Min(ChunkSize, Size - start);
            byte[] buffer;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var mine = cancel.Token; // Also cancelled when a seek leaves this chunk behind.
            lock (_lock)
            {
                buffer = _pool.Count > 0 ? _pool.Pop() : null;
            }

            if (buffer == null)
            {
                buffer = new byte[ChunkSize];
            }

            lock (_lock)
            {
                chunk.Partial = buffer;
                chunk.Received = 0;
                chunk.Cancel = cancel;
            }

            try
            {
                Exception last = null;
                for (var attempt = 1; attempt <= MaxAttempts && !mine.IsCancellationRequested; attempt++)
                {
                    var started = DateTime.UtcNow;
                    lock (_lock)
                    {
                        chunk.Received = 0; // A retry writes the same bytes again from the start.
                    }

                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(mine))
                    try
                    {
                        stall.CancelAfter(StallTimeout);
                        using (var request = new HttpRequestMessage(HttpMethod.Get, _url))
                        {
                            request.Headers.TryAppendWithoutValidation("Range", "bytes=" + start + "-" + (start + length - 1));
                            using (var response = await _client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead).AsTask(stall.Token))
                            {
                                var code = (int)response.StatusCode;
                                if (code == 401 || code == 403 || code == 404 || code == 410)
                                {
                                    throw new LinkExpiredException("HTTP " + code);
                                }

                                if (code != 206)
                                {
                                    throw new InvalidOperationException("HTTP " + code + " (expected 206)");
                                }

                                using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(stall.Token))
                                {
                                    var offset = 0;
                                    while (offset < length)
                                    {
                                        stall.CancelAfter(StallTimeout);
                                        var target = buffer.AsBuffer(offset, 0, length - offset);
                                        var result = await input.ReadAsync(target, (uint)(length - offset), InputStreamOptions.Partial).AsTask(stall.Token);
                                        if (result.Length == 0)
                                        {
                                            throw new InvalidOperationException("connection closed after " + offset + " bytes");
                                        }

                                        if (!ReferenceEquals(result, target))
                                        {
                                            result.CopyTo(0, buffer, offset, (int)result.Length);
                                        }

                                        offset += (int)result.Length;
                                        Interlocked.Add(ref _downloadedBytes, result.Length);
                                        Progressed(chunk, offset);
                                    }
                                }
                            }
                        }

                        TaskCompletionSource<bool> progress;
                        lock (_lock)
                        {
                            chunk.Downloading = false;
                            chunk.Partial = null;
                            chunk.Cancel = null;
                            if (_disposed)
                            {
                                return;
                            }

                            chunk.Data = buffer;
                            chunk.Length = length;
                            chunk.LastUsed = ++_useCounter;
                            progress = chunk.Progress;
                            EvictLocked();
                        }

                        chunk.Ready.TrySetResult(true);
                        progress.TrySetResult(true);
                        var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                        if (chunk.Index < 4 || elapsed > 4000)
                        {
                            _log(elapsed > 4000 ? "warn" : "info", string.Format("Chunk {0}: {1} MB in {2:0} ms ({3:0} Mbps), attempt {4}", chunk.Index, length >> 20, elapsed, length * 8 / elapsed / 1000, attempt));
                        }

                        return;
                    }
                    catch (Exception) when (mine.IsCancellationRequested)
                    {
                        break; // Closing, or a seek made this chunk stale.
                    }
                    catch (Exception) when (stall.IsCancellationRequested)
                    {
                        last = new TimeoutException("no data for " + StallTimeout.TotalSeconds + " s");
                        _log("warn", "Chunk " + chunk.Index + " attempt " + attempt + ": no data for " + StallTimeout.TotalSeconds + " s; retrying");
                    }
                    catch (LinkExpiredException ex)
                    {
                        last = ex;
                        _log("warn", "Chunk " + chunk.Index + ": " + ex.Message + "; resolving the link again");
                        await ReResolveAsync();
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        _log("warn", "Chunk " + chunk.Index + " attempt " + attempt + " failed: " + ex.GetType().Name + " " + ex.Message);
                        try
                        {
                            await Task.Delay(500 * attempt, mine);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                lock (_lock)
                {
                    chunk.Downloading = false;
                    chunk.Partial = null;
                    chunk.Cancel = null;
                    _pool.Push(buffer);
                    if (!mine.IsCancellationRequested && _chunks.TryGetValue(chunk.Index, out var current) && current == chunk)
                    {
                        // Drop it so the next read creates a fresh chunk and tries again.
                        _chunks.Remove(chunk.Index);
                    }
                }

                if (!mine.IsCancellationRequested)
                {
                    _log("error", "Chunk " + chunk.Index + " failed after " + MaxAttempts + " attempts: " + last?.Message);
                    chunk.Ready.TrySetException(new System.IO.IOException("Download failed: " + last?.Message, last));
                }
            }
            finally
            {
                cancel.Dispose();
            }
        }

        // Publishes newly arrived bytes to reads waiting inside this chunk.
        private void Progressed(Chunk chunk, int received)
        {
            TaskCompletionSource<bool> progress = null;
            lock (_lock)
            {
                chunk.Received = received;
                if (chunk.Waiters > 0)
                {
                    progress = chunk.Progress;
                    chunk.Progress = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            progress?.TrySetResult(true);
        }

        // Debrid CDN links expire; resolve the addon URL again (at most every 10 s).
        private async Task ReResolveAsync()
        {
            await _resolving.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _resolvedAt < TimeSpan.FromSeconds(10))
                {
                    return;
                }

                var probe = await ResolveAsync(_sourceUrl, _log);
                _resolvedAt = DateTime.UtcNow;
                if (probe?.FinalUrl != null)
                {
                    _url = probe.FinalUrl;
                }
            }
            finally
            {
                _resolving.Release();
            }
        }

        private sealed class LinkExpiredException : Exception
        {
            public LinkExpiredException(string message)
                : base(message)
            {
            }
        }

        private sealed class Chunk
        {
            public Chunk(long index)
            {
                Index = index;
            }

            public long Index { get; }

            public byte[] Data { get; set; }

            public int Length { get; set; }

            public bool Downloading { get; set; }

            public int Waiters { get; set; }

            public long LastUsed { get; set; }

            /// <summary>Gets or sets the buffer being filled while downloading (null otherwise).</summary>
            public byte[] Partial { get; set; }

            /// <summary>Gets or sets how many bytes of <see cref="Partial"/> are filled, from the start.</summary>
            public int Received { get; set; }

            /// <summary>Gets or sets the download's own cancellation (stopped when a seek leaves it behind).</summary>
            public CancellationTokenSource Cancel { get; set; }

            /// <summary>Gets or sets a task completed (and replaced) whenever more bytes arrive.</summary>
            public TaskCompletionSource<bool> Progress { get; set; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Ready { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>A read-only random-access view of an <see cref="HttpChunkCache"/>, handed to MediaSource.CreateFromStream.</summary>
    public sealed class BufferedHttpStream : IRandomAccessStream
    {
        private readonly HttpChunkCache _cache;
        private readonly int _id;
        private ulong _position;

        public BufferedHttpStream(HttpChunkCache cache, ulong position, string origin = "page", int parent = 0)
        {
            _cache = cache;
            _position = position;
            _id = cache.RegisterView(origin, position, parent);
        }

        public bool CanRead => true;

        public bool CanWrite => false;

        public ulong Position => _position;

        public ulong Size
        {
            get => (ulong)_cache.Size;
            set => throw new NotSupportedException();
        }

        public IInputStream GetInputStreamAt(ulong position) => new BufferedHttpStream(_cache, position, "GetInputStreamAt", _id);

        public IOutputStream GetOutputStreamAt(ulong position) => throw new NotSupportedException();

        public void Seek(ulong position)
        {
            var distance = (long)position - (long)_position;
            if (Math.Abs(distance) > 16L << 20)
            {
                _cache.LogViewEvent(string.Format("View {0} seek {1} MB -> {2} MB", _id, _position >> 20, position >> 20));
            }

            _position = position;
        }

        public IRandomAccessStream CloneStream() => new BufferedHttpStream(_cache, 0, "CloneStream", _id);

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
        {
            return AsyncInfo.Run<IBuffer, uint>(async (token, progress) =>
            {
                count = Math.Min(count, buffer.Capacity);
                uint total = 0;
                var start = (long)_position;
                var watch = Stopwatch.StartNew();
                var seq = _cache.ReadStarted(_id, start);
                try
                {
                    while (total < count)
                    {
                        var read = await _cache.ReadAsync((long)_position, buffer, total, (int)(count - total), token, _id);
                        if (read <= 0)
                        {
                            break;
                        }

                        total += (uint)read;
                        _position += (ulong)read;

                        // Return what is already in memory rather than holding the player on a later chunk
                        // (a byte stream may return fewer bytes than asked; only 0 means end of file). Only at
                        // a chunk boundary: inside a chunk that is still arriving, keep waiting for the bytes
                        // asked for. A few KB handed back mid-chunk made Media Foundation reject the file
                        // (MF_E_UNSUPPORTED_BYTESTREAM_TYPE) on its first header reads, and fail playback
                        // (MF_E_UNEXPECTED, "couldn't be decoded") on a read right after the file opened.
                        var atChunkEnd = _position % (ulong)HttpChunkCache.ChunkSize == 0;
                        if (options == InputStreamOptions.Partial || (atChunkEnd && !_cache.IsAvailable((long)_position)))
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    _cache.ReadFinished(seq, _id, start, (int)total, (int)watch.ElapsedMilliseconds);
                }

                buffer.Length = total;
                return buffer;
            });
        }

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) => throw new NotSupportedException();

        public IAsyncOperation<bool> FlushAsync() => AsyncInfo.Run(token => Task.FromResult(true));

        // The cache is owned and disposed by the page, not by the views the player creates.
        public void Dispose()
        {
            _cache.LogViewEvent(string.Format("View {0} disposed at {1} MB", _id, _position >> 20));
        }
    }
}
