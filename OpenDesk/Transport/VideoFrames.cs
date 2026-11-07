using System.Buffers.Binary;

namespace OpenDesk.Transport;

// One encoded picture from the host: H.264 Annex B, the display generation it was captured for, and the
// host's send time in milliseconds.
public sealed record EncodedFrame(bool Key, long Display, double Timestamp, byte[] Data);

// The `video` channel format written by host.html: flags (bit 0 = keyframe), display generation (float64),
// timestamp (float64), length (uint32), then H.264, all big-endian, split into 60 KB messages. The channel
// is reliable and ordered, so pieces arrive whole and in order.
public sealed class VideoReassembler
{
    public const int Header = 21, Piece = 60000, MaxFrame = 16 * 1024 * 1024;
    private byte[]? pending;
    private int filled;

    // The frame completed by this piece, or null while a frame is still incomplete.
    // Throws InvalidDataException for malformed pieces; the session should then end.
    public EncodedFrame? Add(ReadOnlySpan<byte> piece)
    {
        if (piece.Length == 0 || piece.Length > Piece) throw new InvalidDataException("Invalid video message size.");
        if (pending is null) {
            if (piece.Length < Header) throw new InvalidDataException("Truncated video header.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(piece[17..]);
            if (length == 0 || length > MaxFrame - Header) throw new InvalidDataException("Invalid video frame length.");
            pending = new byte[Header + (int)length]; filled = 0;
        }
        if (filled + piece.Length > pending.Length) throw new InvalidDataException("Video frame overran its length.");
        piece.CopyTo(pending.AsSpan(filled)); filled += piece.Length;
        if (filled < pending.Length) return null;
        var packet = pending; pending = null;
        if (packet[0] > 1) throw new InvalidDataException("Unknown video flags.");
        double display = BinaryPrimitives.ReadDoubleBigEndian(packet.AsSpan(1)), timestamp = BinaryPrimitives.ReadDoubleBigEndian(packet.AsSpan(9));
        if (!double.IsFinite(display) || display < 0 || display != Math.Floor(display) || !double.IsFinite(timestamp)) throw new InvalidDataException("Invalid video header.");
        return new(packet[0] == 1, (long)display, timestamp, packet[Header..]);
    }

    // The same format, for test hosts.
    public static IEnumerable<byte[]> Split(EncodedFrame frame)
    {
        byte[] packet = new byte[Header + frame.Data.Length];
        packet[0] = frame.Key ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleBigEndian(packet.AsSpan(1), frame.Display);
        BinaryPrimitives.WriteDoubleBigEndian(packet.AsSpan(9), frame.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(17), (uint)frame.Data.Length);
        frame.Data.CopyTo(packet, Header);
        for (int offset = 0; offset < packet.Length; offset += Piece) yield return packet[offset..Math.Min(offset + Piece, packet.Length)];
    }
}
