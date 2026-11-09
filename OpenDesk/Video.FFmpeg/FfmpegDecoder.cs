using System.Reflection;
using System.Runtime.InteropServices;
using OpenDesk.Core;

namespace OpenDesk.Video.FFmpeg;

// H.264 decoding through the FFmpeg libraries installed on the system (libavcodec, libavutil, libswscale),
// for desktop viewers. Works with FFmpeg 4 to 8: only the stable public functions and the first fields of
// AVPacket and AVFrame are used. Low-delay mode, slice threads only, so each access unit yields its picture
// at once.
public sealed unsafe class FfmpegDecoder : IVideoDecoder
{
    private const int CodecH264 = 27, PixelBgra = 28, ScaleBilinear = 2, Padding = 64;
    // Offsets that have not changed since FFmpeg 4 (64-bit): AVPacket.data/size, AVFrame.data/linesize/width/height/format.
    private const int PacketData = 24, PacketSize = 32, FrameLinesize = 64, FrameWidth = 104, FrameHeight = 108, FrameFormat = 116;
    private nint context, packet, frame, scaler;
    private byte[] input = new byte[64 * 1024];
    private DecodedPicture? picture;

    public FfmpegDecoder()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The FFmpeg decoder needs a 64-bit process.");
        Native.EnsureLoaded();
        nint codec = Native.avcodec_find_decoder(CodecH264);
        if (codec == 0) throw new PlatformNotSupportedException("This FFmpeg build has no H.264 decoder.");
        context = Native.avcodec_alloc_context3(codec);
        packet = Native.av_packet_alloc(); frame = Native.av_frame_alloc();
        if (context == 0 || packet == 0 || frame == 0) { Dispose(); throw new OutOfMemoryException("FFmpeg could not allocate a decoder."); }
        Native.av_opt_set(context, "flags", "+low_delay", 0);
        Native.av_opt_set(context, "thread_type", "slice", 0);
        Native.av_opt_set(context, "threads", "auto", 0);
        int opened = Native.avcodec_open2(context, codec, 0);
        if (opened < 0) { Dispose(); throw new PlatformNotSupportedException($"FFmpeg could not open the H.264 decoder ({opened})."); }
    }

    public DecodedPicture? Decode(ReadOnlySpan<byte> accessUnit)
    {
        ObjectDisposedException.ThrowIf(context == 0, this);
        if (accessUnit.IsEmpty) return null;
        // FFmpeg may read a little past the end, so the data sits in a zero-padded buffer.
        if (input.Length < accessUnit.Length + Padding) input = new byte[(accessUnit.Length + Padding) * 3 / 2];
        accessUnit.CopyTo(input); input.AsSpan(accessUnit.Length, Padding).Clear();
        fixed (byte* data = input) {
            *(byte**)((byte*)packet + PacketData) = data;
            *(int*)((byte*)packet + PacketSize) = accessUnit.Length;
            int sent = Native.avcodec_send_packet(context, packet);
            *(byte**)((byte*)packet + PacketData) = null; *(int*)((byte*)packet + PacketSize) = 0;
            if (sent < 0 && sent != Native.Again) throw new InvalidDataException($"The video decoder rejected a frame ({sent}).");
        }
        DecodedPicture? result = null;
        // Normally one picture; keep only the newest if the decoder returns more.
        while (Native.avcodec_receive_frame(context, frame) >= 0) {
            result = Convert();
            Native.av_frame_unref(frame);
        }
        return result;
    }

    private DecodedPicture Convert()
    {
        byte* f = (byte*)frame;
        int width = *(int*)(f + FrameWidth), height = *(int*)(f + FrameHeight), format = *(int*)(f + FrameFormat);
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new InvalidDataException("The video has an invalid size.");
        if (picture is null || picture.Width != width || picture.Height != height) picture = new(width, height, width * 4, new byte[width * 4 * height]);
        scaler = Native.sws_getCachedContext(scaler, width, height, format, width, height, PixelBgra, ScaleBilinear, 0, 0, 0);
        if (scaler == 0) throw new InvalidDataException("The video's pixel format is not supported.");
        fixed (byte* pixels = picture.Pixels) {
            byte** destination = stackalloc byte*[4]; int* strides = stackalloc int[4];
            destination[0] = pixels; strides[0] = picture.Stride;
            Native.sws_scale(scaler, (nint)f, (nint)(f + FrameLinesize), 0, height, (nint)destination, (nint)strides);
        }
        return picture;
    }

    public void Dispose()
    {
        if (scaler != 0) { Native.sws_freeContext(scaler); scaler = 0; }
        if (frame != 0) Native.av_frame_free(ref frame);
        if (packet != 0) Native.av_packet_free(ref packet);
        if (context != 0) Native.avcodec_free_context(ref context);
    }

    // Whether FFmpeg's libraries can be found, and a hint for installing them if not.
    public static bool IsAvailable(out string message)
    {
        try { Native.EnsureLoaded(); message = $"FFmpeg libavcodec {Native.Loaded}"; return true; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { message = ex.Message; return false; }
    }

    private static class Native
    {
        // libavcodec major → libavutil and libswscale majors of the same FFmpeg release (4.x to 8.x).
        private static readonly (int Codec, int Util, int Scale)[] Releases = [(62, 60, 9), (61, 59, 8), (60, 58, 7), (59, 57, 6), (58, 56, 5)];
        private static readonly Lock gate = new();
        private static Dictionary<string, nint>? handles;
        public static string Loaded = "";
        public static int Again => OperatingSystem.IsMacOS() ? -35 : -11; // AVERROR(EAGAIN)

        public static void EnsureLoaded()
        {
            lock (gate) {
                if (handles is not null) return;
                foreach (var (codec, util, scale) in Releases) {
                    if (!TryLoad("avcodec", codec, out var c)) continue;
                    if (!TryLoad("avutil", util, out var u) || !TryLoad("swscale", scale, out var s))
                        throw new DllNotFoundException($"FFmpeg's libavcodec {codec} was found, but not the matching libavutil {util} or libswscale {scale}.");
                    handles = new() { ["avcodec"] = c, ["avutil"] = u, ["swscale"] = s };
                    Loaded = codec.ToString();
                    NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => handles.TryGetValue(name, out var h) ? h : 0);
                    av_log_set_level(8); // AV_LOG_FATAL: decode errors are reported as exceptions, not printed
                    return;
                }
                throw new DllNotFoundException(OperatingSystem.IsWindows() ? "FFmpeg was not found. Put the FFmpeg shared DLLs (avcodec, avutil, swscale) next to OpenDesk or set OPENDESK_FFMPEG_DIR."
                    : OperatingSystem.IsMacOS() ? "FFmpeg was not found. Install it with \"brew install ffmpeg\" or set OPENDESK_FFMPEG_DIR."
                    : "FFmpeg was not found. Install your distribution's FFmpeg libraries (for example libavcodec and libswscale) or set OPENDESK_FFMPEG_DIR.");
            }
        }
        private static bool TryLoad(string name, int major, out nint handle)
        {
            string file = OperatingSystem.IsWindows() ? $"{name}-{major}.dll" : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib" : $"lib{name}.so.{major}";
            var folders = new List<string?> { Environment.GetEnvironmentVariable("OPENDESK_FFMPEG_DIR"), AppContext.BaseDirectory };
            if (OperatingSystem.IsMacOS()) folders.AddRange(["/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib"]);
            foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f)))
                if (NativeLibrary.TryLoad(Path.Combine(folder!, file), out handle)) return true;
            return NativeLibrary.TryLoad(file, Assembly.GetExecutingAssembly(), null, out handle);
        }

        [DllImport("avcodec")] public static extern nint avcodec_find_decoder(int id);
        [DllImport("avcodec")] public static extern nint avcodec_alloc_context3(nint codec);
        [DllImport("avcodec")] public static extern int avcodec_open2(nint context, nint codec, nint options);
        [DllImport("avcodec")] public static extern int avcodec_send_packet(nint context, nint packet);
        [DllImport("avcodec")] public static extern int avcodec_receive_frame(nint context, nint frame);
        [DllImport("avcodec")] public static extern void avcodec_free_context(ref nint context);
        [DllImport("avcodec")] public static extern nint av_packet_alloc();
        [DllImport("avcodec")] public static extern void av_packet_free(ref nint packet);
        [DllImport("avutil")] public static extern void av_log_set_level(int level);
        [DllImport("avutil")] public static extern nint av_frame_alloc();
        [DllImport("avutil")] public static extern void av_frame_free(ref nint frame);
        [DllImport("avutil")] public static extern void av_frame_unref(nint frame);
        [DllImport("avutil", CharSet = CharSet.Ansi, BestFitMapping = false)] public static extern int av_opt_set(nint target, string name, string value, int flags);
        [DllImport("swscale")] public static extern nint sws_getCachedContext(nint context, int srcW, int srcH, int srcFormat, int dstW, int dstH, int dstFormat, int flags, nint srcFilter, nint dstFilter, nint param);
        [DllImport("swscale")] public static extern int sws_scale(nint context, nint source, nint sourceStride, int sliceY, int sliceHeight, nint destination, nint destinationStride);
        [DllImport("swscale")] public static extern void sws_freeContext(nint context);
    }
}
