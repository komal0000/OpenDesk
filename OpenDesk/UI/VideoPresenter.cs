using System.Diagnostics;
using System.Threading.Channels;
using OpenDesk.Core;
using OpenDesk.Transport;

namespace OpenDesk.UI;

public delegate void PictureSink(int width, int height, int stride, ReadOnlySpan<byte> bgra);

// Decodes the host's frames on its own thread and keeps only the newest picture for the screen, like
// viewer.html: a decoder that falls behind skips to the next keyframe, and decoder errors ask for one.
public sealed class VideoPresenter : IDisposable
{
    private const int MaxQueued = 2;
    private readonly Func<IVideoDecoder> createDecoder;
    private readonly Func<bool> requestKeyframe;
    private readonly Channel<EncodedFrame> queue = Channel.CreateUnbounded<EncodedFrame>(new() { SingleReader = true });
    private readonly Thread thread;
    private readonly Lock gate = new();
    private byte[] latest = [];
    private int width, height, stride, queued;
    private long display = -1, sequence;
    private bool waitingKey = true;
    private long keyAsked = -10_000;
    // Counters since the last Stats call.
    private int frames, skipped; private double decodeMs; private long bytes;

    // Called on the decode thread after a new picture is ready; the UI then calls CopyLatest.
    public event Action? PictureReady;
    public event Action<string>? Failed;

    public VideoPresenter(Func<IVideoDecoder> createDecoder, Func<bool> requestKeyframe)
    {
        this.createDecoder = createDecoder; this.requestKeyframe = requestKeyframe;
        thread = new Thread(Run) { IsBackground = true, Name = "OpenDesk video decode" };
        thread.Start();
    }

    // From the transport thread: returns at once.
    public void Enqueue(EncodedFrame frame)
    {
        Interlocked.Add(ref bytes, frame.Data.Length);
        // A decoder that falls behind would add lag: drop deltas and wait for a keyframe instead.
        if (!frame.Key && (Volatile.Read(ref waitingKey) || Volatile.Read(ref queued) >= MaxQueued)) {
            Volatile.Write(ref waitingKey, true); Interlocked.Increment(ref skipped); AskKeyframe(); return;
        }
        Volatile.Write(ref waitingKey, false);
        Interlocked.Increment(ref queued);
        queue.Writer.TryWrite(frame);
    }

    private void Run()
    {
        IVideoDecoder decoder;
        try { decoder = createDecoder(); }
        catch (Exception ex) when (ex is DllNotFoundException or PlatformNotSupportedException or EntryPointNotFoundException or OutOfMemoryException) { Failed?.Invoke(ex.Message); return; }
        using (decoder) {
            var reader = queue.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) {
                while (reader.TryRead(out var frame)) {
                    Interlocked.Decrement(ref queued);
                    long started = Stopwatch.GetTimestamp();
                    DecodedPicture? picture;
                    try { picture = decoder.Decode(frame.Data); }
                    catch (InvalidDataException) { Volatile.Write(ref waitingKey, true); AskKeyframe(); continue; }
                    if (picture is null) continue;
                    lock (gate) {
                        if (latest.Length != picture.Pixels.Length) latest = new byte[picture.Pixels.Length];
                        Buffer.BlockCopy(picture.Pixels, 0, latest, 0, picture.Pixels.Length);
                        width = picture.Width; height = picture.Height; stride = picture.Stride; display = frame.Display; sequence++;
                        frames++; decodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                    PictureReady?.Invoke();
                }
            }
        }
    }
    private void AskKeyframe()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref keyAsked) < 250) return;
        Interlocked.Exchange(ref keyAsked, now);
        requestKeyframe();
    }

    // Hands the newest picture to `sink` if it is newer than `since`, and returns its sequence number.
    public long CopyLatest(long since, PictureSink sink, out long shownDisplay)
    {
        lock (gate) {
            shownDisplay = display;
            if (sequence == since || width == 0) return since;
            sink(width, height, stride, latest.AsSpan(0, stride * height));
            return sequence;
        }
    }

    public (int Frames, double DecodeMs, long Bytes, int Skipped) Stats()
    {
        lock (gate) {
            var result = (frames, frames == 0 ? 0 : decodeMs / frames, Interlocked.Exchange(ref bytes, 0), Interlocked.Exchange(ref skipped, 0));
            frames = 0; decodeMs = 0;
            return result;
        }
    }

    public void Dispose() { queue.Writer.TryComplete(); thread.Join(TimeSpan.FromSeconds(2)); }
}
