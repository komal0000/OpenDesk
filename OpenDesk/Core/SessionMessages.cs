using System.Buffers.Binary;

namespace OpenDesk.Core;

public sealed record Approval(bool Accept, bool Control = false, bool Clipboard = false, bool Files = false);
public sealed record SessionStatus(bool Control, long Epoch, string Text, bool Clipboard = false, bool Files = false, bool FrameAcks = false);
public sealed record ViewerInfo(Guid Id, string Name, bool Control);
public sealed record InputEvent(string Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false, long Epoch = 0, int Delta = 0)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && X is >= 0 and <= 1 && Y is >= 0 and <= 1 &&
        Kind switch { "move" or "reset" => true, "button" => Code is >= 1 and <= 3,
            "key" => Code is >= 1 and <= 255, "wheel" => Delta is >= -1200 and <= 1200, _ => false };
}
public interface IInputSink { void Apply(InputEvent input); void ReleaseAll(); }
public sealed record ScreenFrame(int Width, int Height, byte[] Jpeg, long Epoch=0)
{
    public static void Validate(int width, int height, int length)
    {
        if (width is < 1 or > 1600 || height is < 1 or > 8192 || (long)width * height > 16_000_000 ||
            length is < 4 or > Wire.MaxPayload - 17) throw new InvalidDataException("Invalid screen frame.");
    }
    public Message Pack(SessionStatus status)
    {
        Validate(Width, Height, Jpeg.Length);
        byte[] bytes = new byte[17 + Jpeg.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, Width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), Height);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8), status.Epoch);
        bytes[16] = status.Control ? (byte)1 : (byte)0;
        Jpeg.CopyTo(bytes, 17);
        return new(MessageKind.Frame, bytes);
    }
    public static (ScreenFrame Frame, SessionStatus Status) Unpack(Message message)
    {
        if (message.Kind != MessageKind.Frame || message.Payload.Length < 21) throw new InvalidDataException("Missing frame data.");
        var p = message.Payload;
        int w = BinaryPrimitives.ReadInt32BigEndian(p), h = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4));
        Validate(w, h, p.Length - 17);
        if (p[16] > 1) throw new InvalidDataException("Invalid control state.");
        long epoch=BinaryPrimitives.ReadInt64BigEndian(p.AsSpan(8));return (new(w, h, p[17..],epoch), new(p[16] == 1, epoch, "Connected"));
    }
}

public static class ViewerCoordinates
{
    public static (double X, double Y)? Normalize(double x, double y, int areaWidth, int areaHeight, int imageWidth, int imageHeight)
    {
        if (areaWidth <= 0 || areaHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) return null;
        double scale = Math.Min((double)areaWidth / imageWidth, (double)areaHeight / imageHeight);
        double w = imageWidth * scale, h = imageHeight * scale;
        double left = (areaWidth - w) / 2, top = (areaHeight - h) / 2;
        if (x < left || y < top || x >= left + w || y >= top + h) return null;
        return (Math.Clamp((x - left) / w, 0, 1), Math.Clamp((y - top) / h, 0, 1));
    }
    public static (int X, int Y) DesktopPoint(double x, double y, int left, int top, int width, int height) =>
        (left + (int)Math.Round(Math.Clamp(x, 0, 1) * (width - 1)), top + (int)Math.Round(Math.Clamp(y, 0, 1) * (height - 1)));
}
