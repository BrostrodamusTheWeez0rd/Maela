using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Maela
{
    /// <summary>One datagram waiting to go out. Encrypted only when it is sent, so packet counters stay in order.</summary>
    internal struct PacedPacket
    {
        public object Target;
        public byte[] Data;

        public PacedPacket(object target, byte[] data)
        {
            Target = target;
            Data = data;
        }
    }

    /// <summary>
    /// Sends screen-share traffic evenly over time on its own thread. Voice and control packets
    /// never come through here: they go straight to the socket, so they are always first, and
    /// because video can no longer dump a whole frame at once, the router's queue stays short
    /// for them too.
    ///
    /// The sharer's own video and the video a host relays for others are separate flows. Video
    /// is dropped here rather than sent late: a queued frame is useless once a newer keyframe
    /// exists, and a frame that waited too long is dropped along with the frames depending on it.
    /// Frames already part-way out are always finished.
    /// </summary>
    internal sealed class SendScheduler : IDisposable
    {
        public const int PacingFactorPercent = 250;     // Above the average rate, so frames don't wait on each other.
        public const long MinRateBytes = 125000;        // 1 Mbps.
        public const long MaxRateBytes = 25000000;      // 200 Mbps.
        public const int VideoStaleMs = 300;
        public const int RelayStaleMs = 300;
        public const long VideoMaxQueuedBytes = 16 * 1024 * 1024;
        public const long RelayMaxQueuedBytes = 2 * 1024 * 1024;
        public const int ResendStaleMs = 150;           // Later than this, a resent fragment can't arrive in time.
        private const int DrainMs = 66;                 // A backlog (a keyframe, say) should clear within this.
        private const int HeaderBytes = 64;             // Encryption, framing and IP/UDP headers, near enough.
        private const int AverageWindowMs = 250;
        private const int MaxCreditMs = 20;             // Survives a late wake-up without allowing a big burst.
        private const int IdleEndTimerMs = 2000;

        private sealed class Batch
        {
            public PacedPacket[] Packets;
            public int Next;
            public long EnqueuedMs;
            public long Bytes;
            public bool Keyframe;
        }

        private sealed class Flow
        {
            public readonly Queue<Batch> Queue = new Queue<Batch>();
            public Action<object, byte[]> Send;
            public int StaleMs;
            public long MaxQueuedBytes;
            public bool Dependent;      // Frames need the ones before them (not true of opaque relayed packets).
            public long QueuedBytes;
            public long CapBytes;       // 0 = none.
            public double Tokens;
            public long LastMs = -1;
            public long WindowBytes;
            public long WindowStartMs = -1;
            public double AverageBytes;
            public double DrainBytes;   // Rate that clears the backlog in time; held until it has.
        }

        private readonly object _lock = new object();
        private readonly Flow _video = new Flow();
        private readonly Flow _relay = new Flow();
        private readonly Flow _resend = new Flow();
        private readonly List<KeyValuePair<Flow, PacedPacket>> _due = new List<KeyValuePair<Flow, PacedPacket>>();
        private readonly Action _videoDropped;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stop;
        private long _sentPackets, _sentBytes, _droppedFrames, _droppedRelay, _maxWaitMs;

        public SendScheduler(Action<object, byte[]> sendVideo, Action<object, byte[]> sendRelay, Action videoDropped)
        {
            _video.Send = sendVideo;
            _video.StaleMs = VideoStaleMs;
            _video.MaxQueuedBytes = VideoMaxQueuedBytes;
            _video.Dependent = true;
            _relay.Send = sendRelay;
            _relay.StaleMs = RelayStaleMs;
            _relay.MaxQueuedBytes = RelayMaxQueuedBytes;
            _resend.Send = sendVideo;
            _resend.StaleMs = ResendStaleMs;
            _resend.MaxQueuedBytes = RelayMaxQueuedBytes;
            _videoDropped = videoDropped;
        }

        public long SentPackets { get { return Interlocked.Read(ref _sentPackets); } }
        public long SentBytes { get { return Interlocked.Read(ref _sentBytes); } }
        public long DroppedFrames { get { return Interlocked.Read(ref _droppedFrames); } }
        public long DroppedRelayPackets { get { return Interlocked.Read(ref _droppedRelay); } }
        public long MaxWaitMs { get { return Interlocked.Read(ref _maxWaitMs); } }
        public long QueuedVideoBytes { get { lock (_lock) return _video.QueuedBytes; } }

        // ===== Used by the engine (real clock, own thread) =====

        public void EnqueueVideo(PacedPacket[] packets, bool keyframe)
        {
            EnqueueVideoAt(Now(), packets, keyframe);
            Wake();
        }

        public void EnqueueRelay(object target, byte[] data)
        {
            EnqueueRelayAt(Now(), target, data);
            Wake();
        }

        /// <summary>Fragments a viewer asked for again. Kept apart from new video so they aren't stuck behind it.</summary>
        public void EnqueueResend(PacedPacket[] packets)
        {
            if (packets == null || packets.Length == 0)
                return;
            long now = Now();
            lock (_lock)
                Add(_resend, now, packets, false);
            Wake();
        }

        /// <summary>Limits the sharer's video, from what viewers report getting through. 0 = no limit.</summary>
        public void SetVideoCap(long bytesPerSecond)
        {
            lock (_lock)
                _video.CapBytes = bytesPerSecond;
        }

        public void ClearVideo()
        {
            lock (_lock)
            {
                _video.Queue.Clear();
                _video.QueuedBytes = 0;
                _video.DrainBytes = 0;
                _video.CapBytes = 0;
                _resend.Queue.Clear();
                _resend.QueuedBytes = 0;
                _resend.DrainBytes = 0;
            }
        }

        // ===== The logic, with the clock passed in so tests can step through time =====

        internal void EnqueueVideoAt(long nowMs, PacedPacket[] packets, bool keyframe)
        {
            if (packets == null || packets.Length == 0)
                return;
            lock (_lock)
            {
                if (keyframe)
                    DropUnstarted(_video, false); // Nothing older matters once a keyframe is coming.
                Add(_video, nowMs, packets, keyframe);
            }
        }

        internal void EnqueueRelayAt(long nowMs, object target, byte[] data)
        {
            if (data == null)
                return;
            lock (_lock)
                Add(_relay, nowMs, new PacedPacket[] { new PacedPacket(target, data) }, false);
        }

        /// <summary>Sends everything now due. Returns how many packets went out.</summary>
        internal int Step(long nowMs)
        {
            bool dropped = false;
            _due.Clear();
            lock (_lock)
            {
                Collect(_resend, nowMs);
                dropped = Collect(_video, nowMs);
                Collect(_relay, nowMs);
            }
            // Outside the lock: the encoder must never wait for the network.
            int sent = 0;
            foreach (KeyValuePair<Flow, PacedPacket> item in _due)
            {
                try { item.Key.Send(item.Value.Target, item.Value.Data); }
                catch { }
                Interlocked.Increment(ref _sentPackets);
                Interlocked.Add(ref _sentBytes, item.Value.Data.Length);
                sent++;
            }
            _due.Clear();
            if (dropped && _videoDropped != null)
            {
                try { _videoDropped(); } catch { }
            }
            return sent;
        }

        internal long CurrentRateBytes(bool video)
        {
            lock (_lock)
                return Rate(video ? _video : _relay);
        }

        private void Add(Flow flow, long nowMs, PacedPacket[] packets, bool keyframe)
        {
            Batch batch = new Batch();
            batch.Packets = packets;
            batch.EnqueuedMs = nowMs;
            batch.Keyframe = keyframe;
            for (int i = 0; i < packets.Length; i++)
                batch.Bytes += packets[i].Data.Length + HeaderBytes;
            flow.Queue.Enqueue(batch);
            flow.QueuedBytes += batch.Bytes;
            flow.WindowBytes += batch.Bytes;
            // Set here, not from what's left, or the rate would fade as the queue empties and the last packets would crawl.
            flow.DrainBytes = Math.Max(flow.DrainBytes, flow.QueuedBytes * 1000.0 / DrainMs);
        }

        private bool Collect(Flow flow, long nowMs)
        {
            UpdateAverage(flow, nowMs);
            bool dropped = DropOverdue(flow, nowMs);

            long rate = Rate(flow);
            if (flow.LastMs >= 0)
                flow.Tokens += rate * Math.Min(MaxCreditMs, nowMs - flow.LastMs) / 1000.0;
            flow.LastMs = nowMs;
            if (flow.Queue.Count == 0)
            {
                flow.Tokens = Math.Min(flow.Tokens, 0); // Silence doesn't earn a burst later.
                flow.DrainBytes = 0;
                return dropped;
            }

            while (flow.Tokens > 0 && flow.Queue.Count > 0)
            {
                Batch batch = flow.Queue.Peek();
                if (batch.Next == 0)
                    NoteWait(nowMs - batch.EnqueuedMs);
                PacedPacket packet = batch.Packets[batch.Next++];
                long size = packet.Data.Length + HeaderBytes;
                flow.Tokens -= size;
                flow.QueuedBytes -= size;
                _due.Add(new KeyValuePair<Flow, PacedPacket>(flow, packet));
                if (batch.Next >= batch.Packets.Length)
                    flow.Queue.Dequeue();
            }
            return dropped;
        }

        private long Rate(Flow flow)
        {
            double wanted = Math.Max(flow.AverageBytes * PacingFactorPercent / 100.0, flow.DrainBytes);
            long rate = (long)Math.Max(MinRateBytes, Math.Min(MaxRateBytes, wanted));
            if (flow.CapBytes > 0 && rate > flow.CapBytes)
                rate = flow.CapBytes;
            return rate;
        }

        private static void UpdateAverage(Flow flow, long nowMs)
        {
            if (flow.WindowStartMs < 0)
            {
                flow.WindowStartMs = nowMs;
                return;
            }
            long elapsed = nowMs - flow.WindowStartMs;
            if (elapsed < AverageWindowMs)
                return;
            double measured = flow.WindowBytes * 1000.0 / elapsed;
            flow.AverageBytes = flow.AverageBytes == 0 ? measured : flow.AverageBytes * 0.7 + 0.3 * measured;
            flow.WindowBytes = 0;
            flow.WindowStartMs = nowMs;
        }

        private bool DropOverdue(Flow flow, long nowMs)
        {
            bool dropped = false;
            while (flow.Queue.Count > 0)
            {
                Batch head = flow.Queue.Peek();
                if (head.Next > 0)
                {
                    if (flow.Queue.Count == 1)
                        break;
                    // Being sent: always finished, so judge the frames behind it.
                    if (!flow.Dependent)
                        break;
                    return DropBehindStarted(flow, nowMs) || dropped;
                }
                bool stale = nowMs - head.EnqueuedMs > flow.StaleMs && !head.Keyframe;
                bool over = flow.QueuedBytes > flow.MaxQueuedBytes && flow.Queue.Count > 1;
                if (!stale && !over)
                    break;
                if (flow.Dependent)
                {
                    if (DropUnstarted(flow, !over))
                        dropped = true;
                    if (!over || flow.QueuedBytes <= flow.MaxQueuedBytes)
                        break;
                    // Only keyframes were left and there are still too many: lose the oldest.
                    DropHead(flow);
                    dropped = true;
                    continue;
                }
                DropHead(flow);
                Interlocked.Increment(ref _droppedRelay);
            }
            return dropped;
        }

        private bool DropBehindStarted(Flow flow, long nowMs)
        {
            foreach (Batch batch in flow.Queue)
            {
                if (batch.Next > 0 || batch.Keyframe)
                    continue;
                if (nowMs - batch.EnqueuedMs > flow.StaleMs)
                    return DropUnstarted(flow, true);
                break;
            }
            return false;
        }

        /// <summary>Drops waiting frames; with keepKeyframes, deltas only, because they can't be decoded without the one lost.</summary>
        private bool DropUnstarted(Flow flow, bool keepKeyframes)
        {
            if (flow.Queue.Count == 0)
                return false;
            bool any = false;
            int count = flow.Queue.Count;
            for (int i = 0; i < count; i++)
            {
                Batch batch = flow.Queue.Dequeue();
                if (batch.Next > 0 || (keepKeyframes && batch.Keyframe))
                {
                    flow.Queue.Enqueue(batch);
                    continue;
                }
                flow.QueuedBytes -= batch.Bytes;
                Interlocked.Increment(ref _droppedFrames);
                any = true;
            }
            return any;
        }

        private void DropHead(Flow flow)
        {
            Batch batch = flow.Queue.Dequeue();
            flow.QueuedBytes -= batch.Bytes;
            if (flow.Dependent)
                Interlocked.Increment(ref _droppedFrames);
        }

        private void NoteWait(long waited)
        {
            if (waited > Interlocked.Read(ref _maxWaitMs))
                Interlocked.Exchange(ref _maxWaitMs, waited);
        }

        // ===== The thread =====

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long Now()
        {
            return _clock.ElapsedMilliseconds;
        }

        private void Wake()
        {
            if (_thread == null)
            {
                lock (_lock)
                {
                    if (_thread == null && !_stop)
                    {
                        _thread = new Thread(Run);
                        _thread.IsBackground = true;
                        _thread.Name = "Maela send scheduler";
                        _thread.Priority = ThreadPriority.AboveNormal;
                        _thread.Start();
                    }
                }
            }
            _wake.Set();
        }

        private bool HasWork()
        {
            lock (_lock)
                return _video.Queue.Count > 0 || _relay.Queue.Count > 0 || _resend.Queue.Count > 0;
        }

        private void Run()
        {
            bool fineTimer = false;
            long lastBusy = Now();
            try
            {
                while (!_stop)
                {
                    if (HasWork())
                    {
                        if (!fineTimer)
                        {
                            // Windows sleeps can otherwise run 15 ms long, which ruins even pacing.
                            try { timeBeginPeriod(1); fineTimer = true; } catch { }
                        }
                        Step(Now());
                        lastBusy = Now();
                        Thread.Sleep(1);
                        continue;
                    }
                    if (fineTimer && Now() - lastBusy > IdleEndTimerMs)
                    {
                        try { timeEndPeriod(1); } catch { }
                        fineTimer = false;
                    }
                    _wake.WaitOne(fineTimer ? 250 : 2000);
                }
            }
            finally
            {
                if (fineTimer)
                {
                    try { timeEndPeriod(1); } catch { }
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(500); } catch { }
            }
            lock (_lock)
            {
                _video.Queue.Clear();
                _relay.Queue.Clear();
                _resend.Queue.Clear();
                _video.QueuedBytes = 0;
                _relay.QueuedBytes = 0;
                _resend.QueuedBytes = 0;
            }
        }
    }
}
