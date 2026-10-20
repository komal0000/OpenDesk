using System.Runtime.InteropServices;
using static OpenDesk.App.Com;

namespace OpenDesk.App;

// Low-latency H.264 through a Media Foundation encoder MFT. Hardware encoders are tried in system order
// (one that fails to start, such as NVENC on some hybrid laptops, is skipped), then Microsoft's software
// encoder. Hardware MFTs are asynchronous and raise NeedInput/HaveOutput events, polled here without
// blocking; the software one is driven synchronously. Frames arrive as BGRA, are scaled and converted to
// NV12 on the CPU, and only the newest unsent frame is kept, so a busy encoder skips stale frames.
// COM calls go through vtable slots, like DesktopDuplication, to avoid an interop dependency.
internal sealed unsafe class MfEncoder : IDisposable
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a"), Video = new("73646976-0000-0010-8000-00aa00389b71"),
        H264 = new("34363248-0000-0010-8000-00aa00389b71"), Nv12 = new("3231564e-0000-0010-8000-00aa00389b71"),
        MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036"),
        AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207"), Async = new("f81a699a-649a-497d-8c73-29f8fed6ad7a"),
        LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"), FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3"),
        RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423"), MeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52"),
        GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1"), BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2"),
        ForceKey = new("398c1b98-8353-475a-9ef2-8f265d260345"), Transform = new("bf94c121-5b05-4e6f-8000-ba598961414d"),
        EventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"), CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private const int NoEvents = unchecked((int)0xC00D3E80), StreamChange = unchecked((int)0xC00D6D61), NeedMoreInput = unchecked((int)0xC00D6D72), NotAccepting = unchecked((int)0xC00D36B5);
    private const uint NeedInput = 601, HaveOutput = 602, ProvidesSamples = 0x100, CanProvideSamples = 0x200;

    [StructLayout(LayoutKind.Sequential)] private struct TypeInfo { public Guid Major, Sub; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputBuffer { public uint Stream; public nint Sample; public uint Status; public nint Events; }
    [StructLayout(LayoutKind.Sequential)] private struct StreamInfo { public uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)] private struct Variant { public ushort Type; public ushort R1, R2, R3; public long Value; public long Pad; }
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, nint** activates, uint* count);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out nint buffer);

    private static void Check(int hr, string what) { if (hr < 0) throw new IOException($"{what} failed (0x{hr:X8})."); }
    // IMFAttributes: GetUINT32 7, GetAllocatedString 13, SetUINT32 21, SetUINT64 22, SetGUID 24.
    private static int SetU32(nint attributes, Guid key, uint value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value);
    private static int SetU64(nint attributes, Guid key, ulong value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attributes, 22))(attributes, &key, value);
    private static int SetGuid(nint attributes, Guid key, Guid value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value);
    private static uint GetU32(nint attributes, Guid key) { uint value = 0; ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(attributes, 7))(attributes, &key, &value); return value; }
    private static string Text(nint attributes, Guid key)
    {
        char* text; uint length;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint*, int>)Slot(attributes, 13))(attributes, &key, &text, &length) < 0) return "";
        try { return new string(text, 0, (int)length); } finally { Marshal.FreeCoTaskMem((nint)text); }
    }

    internal const int Fps = 60;
    private const long FrameTime = 10_000_000 / Fps; // 100 ns units
    private nint transform, events, codec, pending, reused;
    private readonly bool async;
    private int credit, inFlight, columnsFor;
    private int[] columns = [];
    private long time;
    internal string Name { get; }
    internal int Width { get; }
    internal int Height { get; }
    // A frame is waiting or being encoded: poll for its output rather than wait for the screen.
    internal bool Busy => pending != 0 || inFlight > 0;
    // A new frame would be encoded now rather than replace one still waiting.
    internal bool CanTake => pending == 0 && (!async || credit > 0);

    // Starts the first usable encoder whose name is not in `skip`.
    internal static MfEncoder Create(int width, int height, uint bitrate, IReadOnlySet<string> skip)
    {
        Check(MFStartup(0x20070, 0), "MFStartup");
        var output = new TypeInfo { Major = Video, Sub = H264 };
        var failures = new List<string>();
        foreach (uint flags in new uint[] { 0x4 | 0x40, 0x1 | 0x40 }) { // hardware, then synchronous software; sorted
            nint* activates; uint count;
            if (MFTEnumEx(VideoEncoderCategory, flags, null, &output, &activates, &count) < 0) continue;
            try {
                for (uint i = 0; i < count; i++) {
                    string name = Text(activates[i], FriendlyName);
                    if (skip.Contains(name)) continue;
                    try { return new MfEncoder(activates[i], name, width, height, bitrate); }
                    catch (IOException ex) { failures.Add($"{name}: {ex.Message}"); }
                }
            }
            finally { for (uint i = 0; i < count; i++) { nint a = activates[i]; Release(ref a); } Marshal.FreeCoTaskMem((nint)activates); }
        }
        MFShutdown();
        throw new IOException("No usable H.264 encoder. " + string.Join(" ", failures));
    }

    private MfEncoder(nint activate, string name, int width, int height, uint bitrate)
    {
        Name = name; Width = width; Height = height;
        try {
            Guid iid = Transform; nint created;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(activate, 33))(activate, &iid, &created), "ActivateObject");
            transform = created;
            nint attributes;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(transform, 8))(transform, &attributes), "GetAttributes");
            async = attributes != 0 && GetU32(attributes, Async) != 0;
            if (async) Check(SetU32(attributes, AsyncUnlock, 1), "Unlock async encoder");
            if (attributes != 0) { SetU32(attributes, LowLatency, 1); Release(ref attributes); }
            if (async) Check(Query(transform, EventGenerator, out events), "Event generator");
            // Encoders differ in which settings they accept; a refused one is not fatal.
            if (Query(transform, CodecApi, out codec) >= 0) {
                Set(LowLatency, 11, -1);    // VT_BOOL, VARIANT_TRUE
                Set(BFrames, 19, 0);        // VT_UI4
                Set(RateControl, 19, 0);    // CBR
                Set(MeanBitrate, 19, bitrate);
                Set(GopSize, 19, 0);        // keyframes only on request
            }
            // Encoders take the output type first, then the input type.
            var outType = NewType(H264);
            try {
                SetU32(outType, AverageBitrate, bitrate); SetU32(outType, Profile, 77); // Main
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, 0, outType, 0), "SetOutputType");
            } finally { Release(ref outType); }
            var inType = NewType(Nv12);
            try { Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 15))(transform, 0, inType, 0), "SetInputType"); }
            finally { Release(ref inType); }
            Check(Message(0x10000000), "Begin streaming");
            Check(Message(0x10000003), "Start of stream");
            if (!async) credit = 1;
        }
        catch { Release(ref codec); Release(ref events); Release(ref transform); throw; }
    }
    // A progressive video type of this encoder's size and frame rate.
    private nint NewType(Guid subtype)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");
        SetGuid(type, MajorType, Video); SetGuid(type, Subtype, subtype); SetU32(type, Interlace, 2);
        SetU64(type, FrameSize, (ulong)Width << 32 | (uint)Height); SetU64(type, FrameRate, (ulong)Fps << 32 | 1);
        SetU64(type, PixelAspect, 1UL << 32 | 1);
        return type;
    }
    private int Set(Guid key, ushort type, long value)
    {
        var v = new Variant { Type = type, Value = value };
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Slot(codec, 9))(codec, &key, &v);
    }
    private int Message(uint message) => ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, message, 0);

    // Asks for an IDR frame next. False when the encoder cannot; the caller then restarts it.
    internal bool ForceKeyframe() => codec != 0 && Set(ForceKey, 19, 1) >= 0;

    // Replaces any unsent frame with this one: scaled, converted to NV12, with the cursor drawn at `cursor`
    // (output pixels) when it is not null.
    internal void SetPending(byte* bgra, int pitch, int sourceWidth, int sourceHeight, Point? cursor)
    {
        uint size = (uint)(Width * Height * 3 / 2);
        Check(MFCreateMemoryBuffer(size, out var buffer), "MFCreateMemoryBuffer");
        try {
            byte* data; uint max, current;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &current), "Lock");
            if (columnsFor != sourceWidth) { columns = [.. Enumerable.Range(0, Width).Select(x => x * sourceWidth / Width * 4)]; columnsFor = sourceWidth; }
            ToNv12((nint)bgra, pitch, sourceHeight, (nint)data, Width, Height, columns);
            if (cursor is { } at) DrawCursor(data, Width, Height, at.X, at.Y);
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, size);
            Check(MFCreateSample(out var sample), "MFCreateSample");
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer);
            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time += FrameTime);
            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, FrameTime);
            Release(ref pending); pending = sample;
        }
        finally { Release(ref buffer); }
    }

    // Feeds the pending frame when the encoder takes input and passes out each encoded frame:
    // Annex B H.264 and whether it is a keyframe. Returns false when nothing happened.
    internal bool Pump(Action<byte[], bool> output)
    {
        bool worked = false;
        if (async) {
            while (true) {
                nint ev;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(events, 3))(events, 1, &ev); // MF_EVENT_FLAG_NO_WAIT
                if (hr == NoEvents) break;
                Check(hr, "GetEvent");
                uint type; int status;
                ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(ev, 33))(ev, &type);
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(ev, 35))(ev, &status);
                Release(ref ev);
                Check(status, "Encoder event");
                if (type == NeedInput) credit++;
                else if (type == HaveOutput) { Drain(output); worked = true; }
            }
        }
        if (pending != 0 && (!async || credit > 0)) {
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, 0, pending, 0);
            if (hr != NotAccepting) {
                Check(hr, "ProcessInput");
                Release(ref pending); worked = true; inFlight++;
                if (async) credit--;
            }
            // The synchronous encoder has output once it took input, or when it refuses more.
            if (!async) while (Drain(output)) worked = true;
        }
        return worked;
    }
    // One ProcessOutput call. False when the encoder needs more input.
    private bool Drain(Action<byte[], bool> output)
    {
        StreamInfo info;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, StreamInfo*, int>)Slot(transform, 7))(transform, 0, &info), "GetOutputStreamInfo");
        var result = new OutputBuffer();
        // Encoders that do not supply samples get this one, reused for every frame.
        if ((info.Flags & (ProvidesSamples | CanProvideSamples)) == 0) {
            if (reused == 0) {
                Check(MFCreateSample(out reused), "MFCreateSample");
                Check(MFCreateMemoryBuffer(Math.Max(info.Size, (uint)(Width * Height)), out var space), "MFCreateMemoryBuffer");
                ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(reused, 42))(reused, space); Release(ref space);
            }
            result.Sample = reused;
        }
        uint status;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, OutputBuffer*, uint*, int>)Slot(transform, 25))(transform, 0, 1, &result, &status);
        if (result.Events != 0) Release(ref result.Events);
        nint sample = result.Sample, buffer = 0;
        try {
            if (hr == NeedMoreInput) return false;
            if (hr == StreamChange) {
                // The encoder revised its output type; accept its first offer.
                nint offered;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(transform, 14))(transform, 0, 0, &offered), "GetOutputAvailableType");
                try { Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, 0, offered, 0), "SetOutputType"); }
                finally { Release(ref offered); }
                return true;
            }
            Check(hr, "ProcessOutput");
            if (sample == 0) throw new IOException("The encoder did not provide an output sample.");
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
            byte* data; uint max, length;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length), "Lock output");
            var bytes = new ReadOnlySpan<byte>(data, (int)length).ToArray();
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
            inFlight = Math.Max(0, inFlight - 1);
            if (bytes.Length > 0) output(bytes, IsKeyframe(bytes));
            return true;
        }
        finally { Release(ref buffer); if (sample != reused) Release(ref sample); }
    }
    // An IDR slice (NAL type 5) after any Annex B start code.
    private static bool IsKeyframe(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1f) == 5) return true;
        return false;
    }

    // BT.601 limited range, nearest-neighbour scaling; chroma from the top-left pixel of each 2x2 block.
    // `columns` holds the source byte offset of each output column.
    private static void ToNv12(nint source, int pitch, int sourceHeight, nint target, int width, int height, int[] columns)
    {
        Parallel.For(0, height / 2, row => {
            byte* src = (byte*)source, dst = (byte*)target;
            for (int dy = 0; dy < 2; dy++) {
                int y = row * 2 + dy;
                byte* line = src + (long)(y * sourceHeight / height) * pitch, luma = dst + (long)y * width;
                for (int x = 0; x < width; x++) {
                    byte* p = line + columns[x];
                    luma[x] = (byte)(((66 * p[2] + 129 * p[1] + 25 * p[0] + 128) >> 8) + 16);
                }
            }
            byte* top = src + (long)(row * 2 * sourceHeight / height) * pitch, chroma = dst + (long)width * height + (long)row * width;
            for (int x = 0; x < width; x += 2) {
                byte* p = top + columns[x];
                int r = p[2], g = p[1], b = p[0];
                chroma[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                chroma[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        });
    }

    // The standard arrow (X outline, . fill), since Desktop Duplication leaves the pointer out of the image.
    private static readonly string[] Arrow = [
        "X", "XX", "X.X", "X..X", "X...X", "X....X", "X.....X", "X......X", "X.......X", "X........X",
        "X.....XXXXX", "X..X..X", "X.X X..X", "XX  X..X", "X    X..X", "     X..X", "      X..X", "      X..X", "       XX"];
    private static void DrawCursor(byte* nv12, int width, int height, int left, int top)
    {
        byte* chroma = nv12 + (long)width * height;
        for (int r = 0; r < Arrow.Length; r++) {
            int y = top + r; if (y < 0 || y >= height) continue;
            for (int c = 0; c < Arrow[r].Length; c++) {
                int x = left + c; char cell = Arrow[r][c];
                if (x < 0 || x >= width || cell == ' ') continue;
                nv12[(long)y * width + x] = cell == 'X' ? (byte)16 : (byte)235;
                long uv = (long)(y / 2) * width + (x & ~1);
                chroma[uv] = chroma[uv + 1] = 128;
            }
        }
    }

    public void Dispose()
    {
        Release(ref pending); Release(ref reused); Release(ref codec); Release(ref events); Release(ref transform);
        MFShutdown();
    }
}
