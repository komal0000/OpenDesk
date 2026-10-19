using System.Buffers.Binary;
using System.Text.Json;

namespace OpenDesk.Core;

public enum MessageKind : byte { Hello = 1, Accepted = 2, Status = 3, Frame = 4, Input = 5, Rejected = 6, Ping = 7, Chat = 8, Clipboard = 9, FileOffer = 10, FileChunk = 11, FileFinish = 12, FileAck = 13, FrameAck = 14, Tiles = 15 }
public sealed record Message(MessageKind Kind, byte[] Payload);

public static class Wire
{
    public const int MaxPayload = 8 * 1024 * 1024;
    public const int MaxControl = 16 * 1024;
    private static void Validate(MessageKind kind, int length)
    {
        if (!Enum.IsDefined(kind) || length < 0 || length > (kind is MessageKind.Frame or MessageKind.Tiles ? MaxPayload : MaxControl))
            throw new InvalidDataException("Invalid message type or payload size.");
    }
    public static Message Json<T>(MessageKind kind, T value) => new(kind, JsonSerializer.SerializeToUtf8Bytes(value));
    public static T Decode<T>(Message message) => JsonSerializer.Deserialize<T>(message.Payload) ?? throw new InvalidDataException("Missing message data.");
    public static async Task WriteAsync(Stream stream, Message message, CancellationToken ct)
    {
        Validate(message.Kind, message.Payload.Length);
        if(message.Payload.Length<=MaxControl){
            byte[] packet=new byte[5+message.Payload.Length];packet[0]=(byte)message.Kind;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1),message.Payload.Length);message.Payload.CopyTo(packet,5);
            await stream.WriteAsync(packet,ct);return;
        }
        byte[] header = new byte[5]; header[0] = (byte)message.Kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), message.Payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(message.Payload, ct);
    }
    public static async Task<Message> ReadAsync(Stream stream, CancellationToken ct) => (await ReadTimedAsync(stream, ct)).Message;
    // Also returns when the header arrived, so a viewer can report how long the payload took.
    public static async Task<(Message Message, long HeaderArrived)> ReadTimedAsync(Stream stream, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        var kind = (MessageKind)header[0];
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        Validate(kind, length);
        long arrived = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return (new(kind, payload), arrived);
    }
}
