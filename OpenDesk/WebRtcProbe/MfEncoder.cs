using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenDesk.WebRtcProbe;

// Hardware H.264 through a Media Foundation encoder MFT (NVENC, Quick Sync or AMF, picked by name).
// Hardware MFTs are asynchronous: they raise NeedInput/HaveOutput events, polled here without blocking.
// Frames arrive as BGRA, are scaled and converted to NV12 on the CPU, and only the newest unsent frame
// is kept, so a busy encoder skips stale frames instead of queueing them.
// COM calls go through vtable slots, like DesktopDuplication, to avoid an interop dependency.
internal sealed unsafe class MfEncoder : IDisposable
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a"), Video = new("73646976-0000-0010-8000-00aa00389b71"),
        H264 = new("34363248-0000-0010-8000-00aa00389b71"), Nv12 = new("3231564e-0000-0010-8000-00aa00389b71"),
        MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036"),
        AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207"), LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"),
        FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3"), RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423"),
        MeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52"), GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1"),
        BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2"), Transform = new("bf94c121-5b05-4e6f-8000-ba598961414d"),
        EventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"), CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private const int NoEvents = unchecked((int)0xC00D3E80), StreamChange = unchecked((int)0xC00D6D61), NeedMoreInput = unchecked((int)0xC00D6D72);
    private const uint NeedInput = 601, HaveOutput = 602;

    [StructLayout(LayoutKind.Sequential)] private struct TypeInfo { public Guid Major, Sub; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputBuffer { public uint Stream; public nint Sample; public uint Status; public nint Events; }
    [StructLayout(LayoutKind.Sequential)] private struct Variant { public ushort Type; public ushort R1, R2, R3; public long Value; public long Pad; }
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, nint** activates, uint* count);
    [DllImport("mfplat.dll")] private static extern int MFTEnum2(Guid category, uint flags, TypeInfo* input, TypeInfo* output, nint attributes, nint** activates, uint* count);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out nint attributes, uint size);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [StructLayout(LayoutKind.Sequential)] private struct AdapterDesc1 { public fixed char Description[128]; public uint Vendor, Device, SubSys, Revision; public nuint VideoMemory, SystemMemory, SharedMemory; public ulong Luid; public uint Flags; }
    private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387"), AdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out nint buffer);

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];
    private static void Release(ref nint com) { if (com != 0) { ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com); com = 0; } }
    private static int Query(nint com, Guid iid, out nint result) { nint value; int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(com, 0))(com, &iid, &value); result = value; return hr; }
    private static void Check(int hr, string what) { if (hr < 0) throw new IOException($"{what} failed (0x{hr:X8})."); }
    // IMFAttributes: GetUINT32 7, GetAllocatedString 13, SetUINT32 21, SetUINT64 22, SetGUID 24.
    private static int SetU32(nint attributes, Guid key, uint value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value);
    private static int SetU64(nint attributes, Guid key, ulong value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attributes, 22))(attributes, &key, value);
    private static int SetGuid(nint attributes, Guid key, Guid value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value);
    private static string Text(nint attributes, Guid key)
    {
        char* text; uint length;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint*, int>)Slot(attributes, 13))(attributes, &key, &text, &length) < 0) return "";
        try { return new string(text, 0, (int)length); } finally { Marshal.FreeCoTaskMem((nint)text); }
    }

    private nint transform, events, pending;
    private readonly int width, height;
    private int credit;
    private long pendingTicks;
    private readonly Dictionary<long, long> submitted = [];
    public string Name { get; }
    public List<string> Available { get; } = [];
    public List<double> EncodeMs { get; } = [];
    public List<string> Settings { get; } = [];
    public int Replaced { get; private set; }

    public MfEncoder(string match, int width, int height, int fps, uint bitrate)
    {
        this.width = width; this.height = height;
        Check(MFStartup(0x20070, 0), "MFStartup");
        var output = new TypeInfo { Major = Video, Sub = H264 };
        nint* activates; uint count;
        // On hybrid laptops an encoder may only start when enumerated for its own adapter.
        if (AdapterId(match) is ulong luid) {
            Check(MFCreateAttributes(out var filter, 1), "MFCreateAttributes");
            try {
                SetU64(filter, AdapterLuid, luid);
                Check(MFTEnum2(VideoEncoderCategory, 0x4 | 0x40, null, &output, filter, &activates, &count), "MFTEnum2");
            } finally { Release(ref filter); }
        }
        else Check(MFTEnumEx(VideoEncoderCategory, 0x4 | 0x40, null, &output, &activates, &count), "MFTEnumEx"); // hardware, sorted
        nint chosen = 0; Name = "";
        for (uint i = 0; i < count; i++) {
            string name = Text(activates[i], FriendlyName); Available.Add(name);
            if (chosen == 0 && name.Contains(match, StringComparison.OrdinalIgnoreCase)) { chosen = activates[i]; Name = name; }
        }
        try {
            if (chosen == 0) throw new IOException($"No hardware H.264 encoder matches '{match}'. Found: {string.Join(", ", Available)}");
            Guid iid = Transform; nint created;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(chosen, 33))(chosen, &iid, &created), "ActivateObject");
            transform = created;
        }
        finally { for (uint i = 0; i < count; i++) { nint a = activates[i]; Release(ref a); } Marshal.FreeCoTaskMem((nint)activates); }

        nint attributes;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(transform, 8))(transform, &attributes), "GetAttributes");
        Check(SetU32(attributes, AsyncUnlock, 1), "Unlock async MFT");
        SetU32(attributes, LowLatency, 1);
        Release(ref attributes);
        Check(Query(transform, EventGenerator, out events), "Event generator");

        // Encoders differ in which settings they accept; failures are recorded rather than fatal.
        if (Query(transform, CodecApi, out var codec) >= 0) {
            Set(codec, "low latency", LowLatency, 11, -1);   // VT_BOOL, VARIANT_TRUE
            Set(codec, "no B-frames", BFrames, 19, 0);       // VT_UI4
            Set(codec, "CBR", RateControl, 19, 0);
            Set(codec, "bitrate", MeanBitrate, 19, bitrate);
            Set(codec, "GOP 600", GopSize, 19, 600);
            Release(ref codec);
        }

        // Encoders take the output type first, then the input type.
        Check(MFCreateMediaType(out var outType), "MFCreateMediaType");
        try {
            SetGuid(outType, MajorType, Video); SetGuid(outType, Subtype, H264);
            SetU32(outType, AverageBitrate, bitrate); SetU32(outType, Interlace, 2); SetU32(outType, Profile, 100); // progressive, High
            SetU64(outType, FrameSize, (ulong)width << 32 | (uint)height); SetU64(outType, FrameRate, (ulong)fps << 32 | 1);
            SetU64(outType, PixelAspect, 1UL << 32 | 1);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, 0, outType, 0), "SetOutputType");
        } finally { Release(ref outType); }
        Check(MFCreateMediaType(out var inType), "MFCreateMediaType");
        try {
            SetGuid(inType, MajorType, Video); SetGuid(inType, Subtype, Nv12); SetU32(inType, Interlace, 2);
            SetU64(inType, FrameSize, (ulong)width << 32 | (uint)height); SetU64(inType, FrameRate, (ulong)fps << 32 | 1);
            SetU64(inType, PixelAspect, 1UL << 32 | 1);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 15))(transform, 0, inType, 0), "SetInputType NV12");
        } finally { Release(ref inType); }
        Check(Message(0x10000000), "Begin streaming");
        Check(Message(0x10000003), "Start of stream");
    }
    // LUID of the first DXGI adapter whose description contains `match`.
    private static ulong? AdapterId(string match)
    {
        if (CreateDXGIFactory1(DxgiFactory1, out var factory) < 0) return null;
        try {
            for (uint i = 0; ; i++) {
                nint adapter;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, i, &adapter) < 0) return null; // EnumAdapters1
                try {
                    AdapterDesc1 desc;
                    if (((delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)Slot(adapter, 10))(adapter, &desc) >= 0 && // GetDesc1
                        new string(desc.Description).Contains(match, StringComparison.OrdinalIgnoreCase)) return desc.Luid;
                } finally { Release(ref adapter); }
            }
        } finally { Release(ref factory); }
    }
    private void Set(nint codec, string label, Guid key, ushort type, long value)
    {
        var v = new Variant { Type = type, Value = value };
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Slot(codec, 9))(codec, &key, &v);
        Settings.Add(hr >= 0 ? label : $"{label} rejected 0x{hr:X8}");
    }
    private int Message(uint message) => ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, message, 0);

    // Replaces any unsent frame with this one: scaled to the encoder size and converted to NV12.
    public void SetPending(byte* bgra, int pitch, int sourceWidth, int sourceHeight)
    {
        uint size = (uint)(width * height * 3 / 2);
        Check(MFCreateMemoryBuffer(size, out var buffer), "MFCreateMemoryBuffer");
        try {
            byte* data; uint max, current;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &current), "Lock");
            ToNv12((nint)bgra, pitch, sourceWidth, sourceHeight, (nint)data, width, height);
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, size);
            Check(MFCreateSample(out var sample), "MFCreateSample");
            ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer);
            long ticks = Stopwatch.GetTimestamp(), time = ticks * 10_000_000 / Stopwatch.Frequency;
            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time);
            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, 166_667);
            if (pending != 0) { Release(ref pending); Replaced++; }
            pending = sample; pendingTicks = time;
        }
        finally { Release(ref buffer); }
    }

    // Handles queued encoder events and feeds the pending frame when the encoder asks for one.
    // Output is Annex B H.264 and whether it starts a keyframe. Returns false when nothing happened.
    public bool Pump(Action<byte[], bool, long> output)
    {
        bool worked = false;
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
        if (credit > 0 && pending != 0) {
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, 0, pending, 0), "ProcessInput");
            submitted[pendingTicks] = Stopwatch.GetTimestamp();
            Release(ref pending); credit--; worked = true;
        }
        return worked;
    }
    private void Drain(Action<byte[], bool, long> output)
    {
        var result = new OutputBuffer(); uint status;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, OutputBuffer*, uint*, int>)Slot(transform, 25))(transform, 0, 1, &result, &status);
        if (result.Events != 0) Release(ref result.Events);
        if (hr == NeedMoreInput) return;
        if (hr == StreamChange) {
            // The encoder revised its output type; accept its first offer.
            nint offered;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(transform, 14))(transform, 0, 0, &offered), "GetOutputAvailableType");
            try { Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, 0, offered, 0), "SetOutputType"); }
            finally { Release(ref offered); }
            return;
        }
        Check(hr, "ProcessOutput");
        nint sample = result.Sample, buffer = 0;
        if (sample == 0) throw new IOException("The encoder did not provide an output sample.");
        try {
            long time;
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(sample, 35))(sample, &time);
            if (submitted.Remove(time, out var started)) EncodeMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
            byte* data; uint max, length;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length), "Lock output");
            var bytes = new ReadOnlySpan<byte>(data, (int)length).ToArray();
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
            output(bytes, IsKeyframe(bytes), time / 10);
        }
        finally { Release(ref buffer); Release(ref sample); }
    }
    // An IDR slice (NAL type 5) after any Annex B start code.
    private static bool IsKeyframe(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1f) == 5) return true;
        return false;
    }

    // BT.601 limited range, nearest-neighbour scaling; chroma from the top-left pixel of each 2x2 block.
    private static void ToNv12(nint source, int pitch, int sourceWidth, int sourceHeight, nint target, int width, int height)
    {
        Parallel.For(0, height / 2, row => {
            byte* src = (byte*)source, dst = (byte*)target;
            for (int dy = 0; dy < 2; dy++) {
                int y = row * 2 + dy;
                byte* line = src + (long)(y * sourceHeight / height) * pitch, luma = dst + (long)y * width;
                for (int x = 0; x < width; x++) {
                    byte* p = line + x * sourceWidth / width * 4;
                    luma[x] = (byte)(((66 * p[2] + 129 * p[1] + 25 * p[0] + 128) >> 8) + 16);
                }
            }
            byte* top = src + (long)(row * 2 * sourceHeight / height) * pitch, chroma = dst + (long)width * height + (long)row * width;
            for (int x = 0; x < width; x += 2) {
                byte* p = top + x * sourceWidth / width * 4;
                int r = p[2], g = p[1], b = p[0];
                chroma[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                chroma[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        });
    }

    public void Dispose()
    {
        Release(ref pending); Release(ref events); Release(ref transform);
        MFShutdown();
    }
}
