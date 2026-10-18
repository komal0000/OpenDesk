namespace OpenDesk.Core;

internal sealed class TextBudget
{
    private long start=Environment.TickCount64;
    private int count;
    internal void Check(Message message)
    {
        if(message.Kind is not (MessageKind.Chat or MessageKind.Clipboard))return;
        long now=Environment.TickCount64;if(now-start>=60_000){start=now;count=0;}
        if(++count>60)throw new InvalidDataException("Too many text actions.");
        string text=Wire.Decode<string>(message);
        if(string.IsNullOrEmpty(text)||text.Length>2000||text.Contains('\0'))throw new InvalidDataException("Text must contain 1–2,000 characters.");
    }
}
