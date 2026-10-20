namespace OpenDesk.App;

// `OpenDesk.exe --capture-smoke`: one Desktop Duplication frame through the encoder this PC would use.
internal static unsafe class CaptureSmoke
{
    internal static string Run(Rectangle bounds)
    {
        using var duplication = DesktopDuplication.TryCreate(bounds) ?? throw new IOException($"Desktop Duplication is unavailable for the display at {bounds}.");
        var profile = VideoPipeline.Profiles[1];
        var size = profile.SizeFor(bounds);
        using var encoder = MfEncoder.Create(size.Width, size.Height, profile.Bitrate, new HashSet<string>());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        byte* data; int pitch;
        while (!duplication.TryMap(out data, out pitch, 100) && !duplication.MapCurrent(out data, out pitch))
            if (watch.ElapsedMilliseconds > 3000) throw new IOException("No desktop image arrived within 3 s.");
        try { encoder.SetPending(data, pitch, bounds.Width, bounds.Height, null); } finally { duplication.Unmap(); }
        int bytes = 0; bool key = false;
        while (bytes == 0 && watch.ElapsedMilliseconds < 5000) { if (!encoder.Pump((output, keyframe) => { bytes = output.Length; key = keyframe; })) Thread.Sleep(5); }
        if (bytes == 0) throw new IOException($"{encoder.Name} produced no output within 5 s.");
        return $"PASS {bounds.Width}x{bounds.Height} -> {size.Width}x{size.Height} H.264 with {encoder.Name}: first frame {bytes} bytes, keyframe {key}, {watch.ElapsedMilliseconds} ms";
    }
}
