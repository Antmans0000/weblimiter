using System.Buffers;
using System.Diagnostics;
using WebLimiter.Core.Native;

namespace WebLimiter.Core.Engine;

internal struct HeldPacket
{
    public byte[] Buffer;
    public int Length;
    public WinDivertAddress Address;
    public Counter App;
    public Counter Connection;
}

internal enum Verdict { Pass, Held, Drop }

/// <summary>
/// Token bucket with a bounded delay queue. Packets that exceed the rate are held and
/// released later by the scheduler; when the queue is full they are dropped, which makes
/// TCP back off to the configured rate.
/// </summary>
internal sealed class Shaper
{
    private const int MinBurst = 4 * 1024;
    private const int MinQueue = 64 * 1024;
    private const int MaxQueue = 4 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Queue<HeldPacket> _queue = new();
    private long _rate;          // bytes/s, 0 = unlimited
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();
    private int _queuedBytes;

    public bool HasQueue => Volatile.Read(ref _queuedBytes) > 0;

    public long Rate => Interlocked.Read(ref _rate);

    public void SetRate(long bytesPerSec)
    {
        lock (_lock)
        {
            if (_rate == bytesPerSec) return;
            _rate = Math.Max(0, bytesPerSec);
            _tokens = Math.Min(_tokens, Burst);
        }
    }

    private double Burst => Math.Max(MinBurst, _rate / 20.0);
    private int QueueLimit => (int)Math.Clamp(_rate / 2, MinQueue, MaxQueue);

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        _tokens = Math.Min(Burst, _tokens + _rate * dt);
    }

    public unsafe Verdict Admit(byte* packet, int length, in WinDivertAddress addr, Counter app, Counter connection)
    {
        lock (_lock)
        {
            if (_rate == 0 && _queue.Count == 0) return Verdict.Pass;
            Refill();
            if (_queue.Count == 0 && _tokens > 0)
            {
                _tokens -= length;
                return Verdict.Pass;
            }
            if (_queuedBytes + length > QueueLimit) return Verdict.Drop;

            byte[] buf = ArrayPool<byte>.Shared.Rent(length);
            new ReadOnlySpan<byte>(packet, length).CopyTo(buf);
            _queue.Enqueue(new HeldPacket { Buffer = buf, Length = length, Address = addr, App = app, Connection = connection });
            _queuedBytes += length;
            return Verdict.Held;
        }
    }

    /// <summary>Moves packets whose tokens are available into <paramref name="ready"/>.</summary>
    public void Release(List<HeldPacket> ready, bool discard)
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            Refill();
            while (_queue.Count > 0 && (discard || _rate == 0 || _tokens > 0))
            {
                var p = _queue.Dequeue();
                _queuedBytes -= p.Length;
                if (discard)
                {
                    ArrayPool<byte>.Shared.Return(p.Buffer);
                    continue;
                }
                _tokens -= p.Length;
                ready.Add(p);
            }
        }
    }

    public void Clear()
    {
        var tmp = new List<HeldPacket>();
        Release(tmp, discard: true);
    }
}
