namespace OpenDesk.Core;

// A decoded picture as 32-bit BGRA (alpha 255), Stride bytes per row. The decoder owns Pixels and
// reuses it, so it is valid only until the next Decode call.
public sealed class DecodedPicture(int width, int height, int stride, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public byte[] Pixels { get; } = pixels;
}

// One platform H.264 decoder (Media Foundation, VideoToolbox, MediaCodec or FFmpeg), fed Annex B access
// units in order, without B-frames, so each unit yields at most one picture right away.
public interface IVideoDecoder : IDisposable
{
    // Null while the decoder has no picture yet (it needs a keyframe first).
    // Throws InvalidDataException for data it cannot decode; the caller then waits for a keyframe.
    DecodedPicture? Decode(ReadOnlySpan<byte> accessUnit);
}
