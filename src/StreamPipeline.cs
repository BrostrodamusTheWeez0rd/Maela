using System;
using System.Collections.Generic;
using System.Threading;

namespace Maela
{
    /// <summary>
    /// Hands the newest captured frame from the capture thread to the encoder thread. Capture
    /// never waits: if the encoder is busy, the frame it hadn't started on is replaced (and
    /// reused as the next buffer), so a slow encode costs one frame rather than delaying the rest.
    /// </summary>
    internal sealed class FrameExchange<T> where T : class
    {
        private readonly object _lock = new object();
        private T _pending, _spare;
        private bool _due, _closed;
        private long _replaced;

        public long Replaced { get { lock (_lock) return _replaced; } }

        /// <summary>Capture thread: here's a new frame. Returns a buffer to fill next, or null to make one.</summary>
        public T Publish(T filled)
        {
            lock (_lock)
            {
                T back;
                if (_pending != null)
                {
                    back = _pending;
                    _replaced++;
                }
                else
                {
                    back = _spare;
                    _spare = null;
                }
                _pending = filled;
                _due = true;
                Monitor.PulseAll(_lock);
                return back;
            }
        }

        /// <summary>Capture thread: time for a frame, but the picture hasn't changed.</summary>
        public void Tick()
        {
            lock (_lock)
            {
                _due = true;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>
        /// Encoder thread: waits for a frame to be due. Puts the newest picture in
        /// <paramref name="current"/> (which stays as it was if nothing changed).
        /// </summary>
        public bool Take(ref T current, int timeoutMs)
        {
            lock (_lock)
            {
                if (!_due && !_closed)
                    Monitor.Wait(_lock, timeoutMs);
                if (!_due || _closed)
                    return false;
                _due = false;
                if (_pending != null)
                {
                    _spare = current;
                    current = _pending;
                    _pending = null;
                }
                return current != null;
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// Shows frames on an even beat even though the network delivers them unevenly. Waits only
    /// as long as the connection's recent unevenness calls for, plays slightly fast to work off
    /// a backlog, and drops the oldest frame if too many pile up.
    /// </summary>
    internal sealed class JitterBuffer<T> where T : class
    {
        public const int MaxDelayMs = 100;
        private const double PlayoutFactor = 2.5;

        private struct Entry
        {
            public T Item;
            public long ArrivalMs;
        }

        private readonly Queue<Entry> _queue = new Queue<Entry>();
        private readonly int _maxFrames;
        private double _interval, _jitter;
        private long _lastArrival = -1, _nextDue = -1;
        private bool _starved;

        /// <summary>Called for frames the buffer drops, in case they hold resources.</summary>
        public Action<T> Discard;
        public long Dropped { get; private set; }
        public long Underruns { get; private set; }
        public int Count { get { return _queue.Count; } }
        public double IntervalMs { get { return _interval; } }
        public double JitterMs { get { return _jitter; } }
        public int TargetDelayMs { get { return (int)Math.Min(MaxDelayMs, _jitter * PlayoutFactor); } }

        public JitterBuffer(double expectedIntervalMs, int maxFrames)
        {
            _interval = expectedIntervalMs;
            _maxFrames = Math.Max(2, maxFrames);
        }

        public void Push(long nowMs, T item)
        {
            if (_lastArrival >= 0)
            {
                long delta = nowMs - _lastArrival;
                if (delta < 500) // Longer is the sender pausing, not jitter.
                {
                    _interval = Math.Max(4, Math.Min(100, _interval + (delta - _interval) * 0.05));
                    _jitter += (Math.Abs(delta - _interval) - _jitter) * 0.1;
                }
            }
            _lastArrival = nowMs;
            Entry entry = new Entry();
            entry.Item = item;
            entry.ArrivalMs = nowMs;
            _queue.Enqueue(entry);
            while (_queue.Count > _maxFrames)
            {
                Entry oldest = _queue.Dequeue();
                Dropped++;
                if (Discard != null)
                    Discard(oldest.Item);
            }
        }

        /// <summary>The frame to show now, or null.</summary>
        public T Pop(long nowMs)
        {
            if (_queue.Count == 0)
            {
                if (_nextDue >= 0 && nowMs >= _nextDue && !_starved)
                {
                    _starved = true;
                    Underruns++;
                    _nextDue = -1; // Build the cushion again before carrying on.
                }
                return null;
            }
            _starved = false;
            if (_nextDue < 0)
                _nextDue = _queue.Peek().ArrivalMs + TargetDelayMs;
            if (nowMs < _nextDue)
                return null;

            Entry entry = _queue.Dequeue();
            double backlogMs = _queue.Count * _interval - TargetDelayMs;
            double step = _interval;
            if (backlogMs > _interval * 3)
                step *= 0.75;
            else if (backlogMs > _interval)
                step *= 0.9;
            _nextDue += (long)Math.Max(1, Math.Round(step));
            if (_nextDue < nowMs - (long)_interval)
                _nextDue = nowMs; // The display fell behind: don't then rush to catch up.
            return entry.Item;
        }
    }

    /// <summary>
    /// Chooses the encoder's bitrate. Quality only falls on evidence of congestion (loss, or the
    /// connection's delay rising, which comes first); a quiet screen sending little isn't evidence.
    /// It then climbs back gently, after a pause.
    /// </summary>
    internal sealed class BitrateController
    {
        public const int MinKbps = 500;
        private const int HeavyLossPermille = 100;
        private const int LossPermille = 30;
        private const int DelayRiseMs = 60;
        private const int DecreaseGapMs = 1000;
        private const int ClimbHoldMs = 3000;
        private const int ClimbGapMs = 500;

        private int _maxKbps;
        private double _kbps;
        private long _lastDecreaseMs = long.MinValue / 2, _lastClimbMs = long.MinValue / 2;

        public BitrateController(int maxKbps)
        {
            _maxKbps = Math.Max(MinKbps, maxKbps);
            _kbps = _maxKbps;
        }

        public int CurrentKbps { get { return (int)_kbps; } }

        public void SetMax(int maxKbps)
        {
            bool atTop = _kbps >= _maxKbps;
            _maxKbps = Math.Max(MinKbps, maxKbps);
            if (atTop || _kbps > _maxKbps)
                _kbps = _maxKbps;
        }

        public void Update(long nowMs, int lossPermille, int delayRiseMs)
        {
            double factor = lossPermille > HeavyLossPermille ? 0.7 : lossPermille > LossPermille ? 0.85 : delayRiseMs > DelayRiseMs ? 0.9 : 1.0;
            if (factor < 1.0)
            {
                if (nowMs - _lastDecreaseMs >= DecreaseGapMs)
                {
                    _kbps = Math.Max(MinKbps, _kbps * factor); // Reports need a moment to show the last cut.
                    _lastDecreaseMs = nowMs;
                }
                return;
            }
            if (nowMs - _lastDecreaseMs < ClimbHoldMs || nowMs - _lastClimbMs < ClimbGapMs)
                return;
            _lastClimbMs = nowMs;
            _kbps = Math.Min(_maxKbps, _kbps * 1.05 + 100);
        }
    }

    /// <summary>XOR error correction: one parity packet rebuilds any one fragment lost from its group.</summary>
    internal static class FecXor
    {
        public static void Mix(byte[] parity, int parityOffset, byte[] data, int dataOffset, int length)
        {
            for (int i = 0; i < length; i++)
                parity[parityOffset + i] ^= data[dataOffset + i];
        }

        /// <summary>How many fragments share one parity packet at this loss; 0 means no parity is worth sending.</summary>
        public static int GroupSizeForLoss(int lossPermille)
        {
            return lossPermille < 5 ? 0 : lossPermille < 30 ? 16 : lossPermille < 80 ? 8 : 4;
        }
    }
}
