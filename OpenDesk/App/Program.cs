namespace OpenDesk.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.AddMessageFilter(new RemoteInputFilter());
        if (args.Contains("--capture-smoke")) {
            try {
                using var capture=new ScreenCapture(Screen.PrimaryScreen!.Bounds);var frame = capture.Capture();
                var dimensions = OpenDesk.Core.JpegDimensions.Read(frame.Jpeg);
                if (dimensions != (frame.Width, frame.Height)) throw new InvalidDataException("Capture dimensions differ.");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "capture-smoke.txt"), $"PASS {frame.Width}x{frame.Height}, {frame.Jpeg.Length} encoded bytes");
            } catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "capture-smoke.txt"), "FAIL " + ex.GetType().Name); Environment.ExitCode = 1; }
            return;
        }
        using var main = new MainForm(!args.Any(a => a is "--smoke" or "--ui-smoke"));
        if (args.Contains("--smoke") || args.Contains("--ui-smoke")) {
            main.ShowInTaskbar = false; main.Opacity = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) => {
                timer.Stop(); timer.Dispose();
                if (args.Contains("--ui-smoke")) {
                    using var bitmap = new Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, main.Size));
                    bitmap.Save(Path.Combine(AppContext.BaseDirectory, "ui-smoke.png"));
                }
                main.Close();
            };
            main.Shown += (_, _) => timer.Start();
        }
        Application.Run(main);
    }
}
