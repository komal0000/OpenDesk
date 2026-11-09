namespace OpenDesk.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.AddMessageFilter(new RemoteInputFilter());
        // Checks this PC can capture and encode the primary display; reports which H.264 encoder it uses.
        if (args.Contains("--capture-smoke")) {
            string result;
            try { result = CaptureSmoke.Run(Screen.PrimaryScreen!.Bounds); }
            catch (Exception ex) { result = "FAIL " + ex.Message; Environment.ExitCode = 1; }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "capture-smoke.txt"), result);
            return;
        }
        bool smoke=args.Any(a=>a is "--smoke" or "--ui-smoke");
        var fixture=smoke?new ClientSettings{DeviceId="123456789",DisplayName="OpenDesk preview",StorageFile=Path.Combine(Path.GetTempPath(),"OpenDesk-ui-"+Guid.NewGuid().ToString("N"),"profile.json")}:null;
        using var main = new MainForm(!smoke,fixture);
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
