using System.Buffers.Binary;

namespace OpenDesk.Core;

public sealed record Approval(bool Accept, bool Control = false, bool Clipboard = false, bool Files = false);
// Epoch changes with every control or display change; Display only when the shared monitor changes,
// so a viewer knows which video frames match the coordinates it sends.
public sealed record SessionStatus(bool Control, long Epoch, string Text, bool Clipboard = false, bool Files = false, bool FrameAcks = false, bool Tiles = false, long Display = 0)
{
    public static bool IsFile(MessageKind kind) => kind is >= MessageKind.FileOffer and <= MessageKind.FileAck;
    // Clipboard text and files need the host's approval; everything else is always allowed.
    public bool Allows(MessageKind kind) => kind == MessageKind.Clipboard ? Clipboard : !IsFile(kind) || Files;
}
public sealed record ViewerInfo(Guid Id, string Name, bool Control);
// Key: W3C KeyboardEvent.code ("KeyA", "ShiftLeft"), a physical key position; when set, Code is unused.
public sealed record InputEvent(string Kind, double X = 0, double Y = 0, int Code = 0, bool Down = false, long Epoch = 0, int Delta = 0, string? Key = null)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && X is >= 0 and <= 1 && Y is >= 0 and <= 1 &&
        Kind switch { "move" or "reset" => true, "button" => Code is >= 1 and <= 3,
            "key" => Key is null ? Code is >= 1 and <= 255 : Key.Length is >= 2 and <= 24 && Key.All(char.IsAsciiLetterOrDigit),
            "wheel" => Delta is >= -1200 and <= 1200, _ => false };
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

// Host screen that can report which tiles changed, so viewers receive only what changed.
public interface IScreenSource
{
    long Capture();                    // content sequence; unchanged while the screen is unchanged
    ScreenFrame Frame();               // whole latest image, for viewers without tile support
    TileUpdate Changes(long since);    // tiles changed after `since`
}

// Changed square tiles of a Width x Height image, packed left to right into one JPEG atlas.
// Tiles holds row * Columns + column for each atlas slot. No tiles means a status-only update.
public sealed record TileUpdate(int Width, int Height, int TileSize, ushort[] Tiles, byte[] Jpeg, long Sequence = 0, long Epoch = 0)
{
    private const int Header = 21;
    public int Columns => (Width + TileSize - 1) / TileSize;
    public int Rows => (Height + TileSize - 1) / TileSize;
    public (int Columns, int Rows) Atlas { get { int c = Math.Min(Tiles.Length, Columns); return c == 0 ? (0, 0) : (c, (Tiles.Length + c - 1) / c); } }
    // Where slot i sits in the atlas, and the image area its tile covers.
    public (int AtlasX, int AtlasY, int X, int Y, int Width, int Height) Slot(int i)
    {
        int atlasColumns = Atlas.Columns, x = Tiles[i] % Columns * TileSize, y = Tiles[i] / Columns * TileSize;
        return (i % atlasColumns * TileSize, i / atlasColumns * TileSize, x, y, Math.Min(TileSize, this.Width - x), Math.Min(TileSize, this.Height - y));
    }
    private void Validate()
    {
        ScreenFrame.Validate(Width, Height, 4);
        if (TileSize is < 16 or > 256 || TileSize % 16 != 0 || Columns * TileSize > 1600 || Rows * TileSize > 8192 ||
            Tiles.Length > Columns * Rows || (Tiles.Length == 0) != (Jpeg.Length == 0) || Jpeg.Length is > 0 and < 4 ||
            Header + Tiles.Length * 2L + Jpeg.Length > Wire.MaxPayload || Tiles.Any(t => t >= Columns * Rows)) throw new InvalidDataException("Invalid screen update.");
    }
    public Message Pack(SessionStatus status)
    {
        Validate();
        byte[] bytes = new byte[Header + Tiles.Length * 2 + Jpeg.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, Width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), Height);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8), status.Epoch);
        bytes[16] = status.Control ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(17), (ushort)TileSize);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(19), (ushort)Tiles.Length);
        for (int i = 0; i < Tiles.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(Header + i * 2), Tiles[i]);
        Jpeg.CopyTo(bytes, Header + Tiles.Length * 2);
        return new(MessageKind.Tiles, bytes);
    }
    public static (TileUpdate Update, SessionStatus Status) Unpack(Message message)
    {
        var p = message.Payload;
        if (message.Kind != MessageKind.Tiles || p.Length < Header) throw new InvalidDataException("Missing screen update.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(19));
        if (p[16] > 1 || p.Length < Header + count * 2) throw new InvalidDataException("Invalid screen update.");
        var tiles = new ushort[count];
        for (int i = 0; i < count; i++) tiles[i] = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(Header + i * 2));
        long epoch = BinaryPrimitives.ReadInt64BigEndian(p.AsSpan(8));
        var update = new TileUpdate(BinaryPrimitives.ReadInt32BigEndian(p), BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4)),
            BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(17)), tiles, p[(Header + count * 2)..], 0, epoch);
        update.Validate();
        return (update, new(p[16] == 1, epoch, "Connected"));
    }
}

public static class ViewerCoordinates
{
    // Where the viewer paints an image scaled to fit the area and centred; clicks map through the same pixels.
    public static (int X, int Y, int Width, int Height) Fit(int areaWidth, int areaHeight, int imageWidth, int imageHeight)
    {
        double scale = Math.Min((double)areaWidth / imageWidth, (double)areaHeight / imageHeight);
        int w = Math.Max(1, (int)Math.Round(imageWidth * scale)), h = Math.Max(1, (int)Math.Round(imageHeight * scale));
        return ((areaWidth - w) / 2, (areaHeight - h) / 2, w, h);
    }
    public static (double X, double Y)? Normalize(double x, double y, int areaWidth, int areaHeight, int imageWidth, int imageHeight)
    {
        if (areaWidth <= 0 || areaHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) return null;
        var (left, top, w, h) = Fit(areaWidth, areaHeight, imageWidth, imageHeight);
        if (x < left || y < top || x >= left + w || y >= top + h) return null;
        return (Math.Clamp((x - left) / w, 0, 1), Math.Clamp((y - top) / h, 0, 1));
    }
    public static (int X, int Y) DesktopPoint(double x, double y, int left, int top, int width, int height) =>
        (left + (int)Math.Round(Math.Clamp(x, 0, 1) * (width - 1)), top + (int)Math.Round(Math.Clamp(y, 0, 1) * (height - 1)));
}
