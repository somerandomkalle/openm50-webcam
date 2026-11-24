namespace OpenM50Webcam.Server;

internal sealed record Frame(long Sequence, byte[] Jpeg, int Width, int Height);

/// <summary>Holds the latest frame and lets any number of readers wait for the next one.</summary>
internal sealed class FrameHub
{
    private readonly object _lock = new();
    private TaskCompletionSource<Frame> _next = NewTcs();
    private Frame? _latest;
    private long _sequence;

    // fps measurement
    private int _framesInWindow;
    private DateTime _windowStart = DateTime.UtcNow;
    public double Fps { get; private set; }

    private long _lastFrameTicks = -1; // Environment.TickCount64 of the last frame

    public Frame? Latest
    {
        get { lock (_lock) return _latest; }
    }

    public long MillisecondsSinceLastFrame
    {
        get
        {
            long t = Interlocked.Read(ref _lastFrameTicks);
            return t < 0 ? long.MaxValue / 2 : Environment.TickCount64 - t;
        }
    }

    public void Publish(byte[] jpeg)
    {
        var (w, h) = Jpeg.GetSize(jpeg);
        TaskCompletionSource<Frame> tcs;
        Frame frame;
        lock (_lock)
        {
            frame = new Frame(++_sequence, jpeg, w, h);
            _latest = frame;
            Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);
            tcs = _next;
            _next = NewTcs();

            _framesInWindow++;
            var elapsed = DateTime.UtcNow - _windowStart;
            if (elapsed.TotalSeconds >= 1)
            {
                Fps = _framesInWindow / elapsed.TotalSeconds;
                _framesInWindow = 0;
                _windowStart = DateTime.UtcNow;
            }
        }
        tcs.TrySetResult(frame);
    }

    public void MarkIdle()
    {
        lock (_lock)
        {
            Fps = 0;
            _framesInWindow = 0;
            _windowStart = DateTime.UtcNow;
        }
    }

    /// <summary>Returns a frame newer than <paramref name="afterSequence"/>, waiting if needed.</summary>
    public async Task<Frame> NextAsync(long afterSequence, CancellationToken ct)
    {
        Task<Frame> wait;
        lock (_lock)
        {
            if (_latest is not null && _latest.Sequence > afterSequence) return _latest;
            wait = _next.Task;
        }
        return await wait.WaitAsync(ct).ConfigureAwait(false);
    }

    private static TaskCompletionSource<Frame> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class Jpeg
{
    /// <summary>Reads width/height from the SOFn marker. Returns (0,0) if not found.</summary>
    public static (int Width, int Height) GetSize(ReadOnlySpan<byte> d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            int len = (d[i + 2] << 8) | d[i + 3];
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
                return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
            if (marker == 0xDA) break; // start of scan
            i += 2 + len;
        }
        return (0, 0);
    }
}
