using System.Diagnostics;

namespace OpenDesk.Core;

// Keeps unacknowledged video near one bandwidth-delay product: enough to keep a slow, distant
// link busy, never so much that stale frames queue in front of new ones. The viewer reports how
// long each frame took to arrive and how long it held it, so the host can measure the link rate
// and the path delay separately from frame size. Each large frame measures the link rate by itself,
// so no bandwidth probing is needed; sending while below budget lets one frame overlap the next.
public sealed class FramePacer
{
    private const long Floor = 32 * 1024, Unmeasured = 128 * 1024;
    private const int TlsRecord = 16 * 1024;
    private readonly object gate = new();
    private readonly Queue<(int Bytes, double Sent)> flight = new();
    private long inFlight;
    private double minPath = double.MaxValue, minPathAt, maxRate, maxRateAt;
    public static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
    public bool CanSend() { lock (gate) return flight.Count == 0 || inFlight < (maxRate == 0 ? Unmeasured : Math.Max(Floor, (long)(maxRate * minPath))); }
    public void OnSend(int bytes, double now) { lock (gate) { flight.Enqueue((bytes, now)); inFlight += bytes; } }
    // receiveMs: frame header to payload at the viewer. holdMs: frame header to this acknowledgment.
    public bool OnAck(double now, double receiveMs, double holdMs)
    {
        lock (gate) {
            if (!flight.TryDequeue(out var frame)) return false;
            inFlight -= frame.Bytes;
            // Round trip minus the viewer's own time: propagation plus any queue ahead of this frame.
            double path = Math.Max(1, now - frame.Sent - holdMs);
            if (path <= minPath || now - minPathAt > 10_000) { minPath = path; minPathAt = now; }
            // After its first TLS record, a large frame arrives at the slowest link's rate.
            if (frame.Bytes > 2 * TlsRecord && receiveMs >= 2) {
                double rate = (frame.Bytes - TlsRecord) / receiveMs;
                if (rate >= maxRate || now - maxRateAt > 3_000) { maxRate = rate; maxRateAt = now; }
            }
            return true;
        }
    }
}
