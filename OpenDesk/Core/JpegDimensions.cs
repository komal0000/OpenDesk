using System.Buffers.Binary;

namespace OpenDesk.Core;

public static class JpegDimensions
{
    // Read SOF before passing encoded data to GDI+, so advertised sizes cannot hide a huge bitmap.
    public static (int Width, int Height) Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) throw new InvalidDataException("Not a JPEG frame.");
        int pos = 2;
        while (pos < bytes.Length) {
            if (bytes[pos++] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            while (pos < bytes.Length && bytes[pos] == 0xff) pos++;
            if (pos >= bytes.Length) break;
            byte marker = bytes[pos++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is >= 0xd0 and <= 0xd8 or 0x01) continue;
            if (pos + 2 > bytes.Length) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[pos..]);
            if (length < 2 || pos + length > bytes.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2) {
                if (length < 8 || bytes[pos + 2] != 8) break;
                int h = BinaryPrimitives.ReadUInt16BigEndian(bytes[(pos + 3)..]);
                int w = BinaryPrimitives.ReadUInt16BigEndian(bytes[(pos + 5)..]);
                ScreenFrame.Validate(w, h, bytes.Length);
                return (w, h);
            }
            pos += length;
        }
        throw new InvalidDataException("JPEG dimensions are missing or unsupported.");
    }
}
