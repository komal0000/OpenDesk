using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using OpenDesk.App;
using OpenDesk.Core;

namespace OpenDesk.Smoke;

// Outside judge for video speed: real screen capture, real TLS session, emulated relay link.
internal static class PerfChecks
{
    private static Rectangle Screen0=>Screen.PrimaryScreen!.Bounds;
    internal static async Task RunAsync()
    {
        var lines=new List<string>{$"Screen {Screen0.Width}x{Screen0.Height}, {Environment.ProcessorCount} logical CPUs, {DateTime.Now:yyyy-MM-dd HH:mm}"};
        using var probe=new Probe();probe.Show();using var doc=new ScrollingDocument();
        var ticker=new System.Windows.Forms.Timer{Interval=30};ticker.Tick+=(_,_)=>{probe.Next();if(doc.Visible)doc.Advance();};ticker.Start();
        await Task.Delay(300);
        foreach(var (name,large) in new[]{("idle",false),("small",false),("large",true)}){
            ticker.Enabled=name!="idle";doc.Visible=large;await Task.Delay(200);
            lines.Add(await HostStages(name));
        }
        ticker.Enabled=true;doc.Visible=false;
        lines.Add(await ViewerStages(false));doc.Visible=true;await Task.Delay(200);lines.Add(await ViewerStages(true));
        foreach(var (name,oneWay,mbps) in new[]{("loopback",0,0d),("relay 500ms RTT 8Mbps",250,8d),("relay 500ms RTT 2Mbps",250,2d)})
            foreach(bool large in new[]{false,true}){doc.Visible=large;await Task.Delay(200);lines.Add(await EndToEnd(name,oneWay,mbps,large,probe));}
        ticker.Stop();doc.Visible=false;
        lines.Add(ResampleRegionsMatchWhole());lines.Add(await ViewerMatchesScreen(ticker,doc));
        probe.Close();doc.Close();
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"perf-results.txt"),lines);
        if(lines.Any(l=>l.StartsWith("FAIL")))throw new Exception(string.Join(Environment.NewLine,lines.Where(l=>l.StartsWith("FAIL"))));
    }
    // Region-limited resampling (used with dirty rectangles) must equal resampling everything.
    private static unsafe string ResampleRegionsMatchWhole()
    {
        var random=new Random(7);int sw=1920,sh=1080,w=1280,h=720;var source=new byte[sw*sh*4];random.NextBytes(source);
        var whole=new byte[w*h*4];var parts=new byte[w*h*4];
        fixed(byte* s=source)fixed(byte* a=whole)fixed(byte* b=parts){
            Resample.Bilinear(s,sw*4,sw,sh,a,w*4,w,h);
            for(int i=0;i<200;i++){var r=new Rectangle(random.Next(sw-50),random.Next(sh-50),random.Next(1,50),random.Next(1,50));Resample.Bilinear(s,sw*4,sw,sh,b,w*4,w,h,Resample.Affected(r,sw,sh,w,h));}
            for(int y=0;y<sh;y+=40)Resample.Bilinear(s,sw*4,sw,sh,b,w*4,w,h,Resample.Affected(new(0,y,sw,40),sw,sh,w,h));
        }
        return whole.AsSpan().SequenceEqual(parts)?"PASS region resampling equals whole-image resampling":"FAIL region resampling differs from whole-image resampling";
    }
    // After motion stops, what the viewer shows must match the host screen read independently
    // through GDI (no duplication, no tiles, no JPEG). Catches black, stale or misplaced tiles.
    private static async Task<string> ViewerMatchesScreen(System.Windows.Forms.Timer ticker,ScrollingDocument doc)
    {
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var desktop=new DesktopSource(Screen0);
        await using var host=new HostService(desktop,new NoInput(),(_,_)=>Task.FromResult(new Approval(true)));
        host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
        await using var viewer=await ViewerSession.ConnectAsync(host.Invitation,"Check",ct.Token,true);
        using var screen=new RemoteScreen();
        var read=Task.Run(()=>viewer.RunAsync(screen.Apply,_=>{},ct.Token,screen.Apply));
        doc.Visible=true;ticker.Start();await Task.Delay(1500,ct.Token);ticker.Stop();doc.Visible=false;await Task.Delay(800,ct.Token);
        // Three screen samples around the viewer read: a caret or spinner caught mid-blink matches one
        // of them, while a stale, black or misplaced tile matches none.
        // The host draws the cursor into the stream but GDI never captures it: skip every place it was.
        var size=screen.ImageSize;double s=(double)size.Width/Screen0.Width;var pointers=new List<Rectangle>();
        void Pointer(){var c=Cursor.Position;pointers.Add(new((int)((c.X-Screen0.X)*s)-12,(int)((c.Y-Screen0.Y)*s)-12,48,48));}
        Pointer();var before=GdiTruth(size);await Task.Delay(400,ct.Token);Pointer();
        var shown=new uint[size.Width*size.Height];for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)shown[y*size.Width+x]=(uint)screen.Pixel(x,y).ToArgb();
        Pointer();var truth=GdiTruth(size);var ideal=JpegRoundTrip(truth,size);await Task.Delay(300,ct.Token);var after=GdiTruth(size);Pointer();
        ct.Cancel();try{await read;}catch(Exception ex)when(ex is OperationCanceledException or IOException or ObjectDisposedException){}
        int checkedTiles=0,live=0,bad=0;double sumShown=0,sumIdeal=0;string worst="";double worstExcess=double.MinValue;Rectangle worstTile=default;
        for(int ty=0;ty<size.Height;ty+=32)for(int tx=0;tx<size.Width;tx+=32){
            var tile=Rectangle.Intersect(new(tx,ty,32,32),new(Point.Empty,size));if(pointers.Any(tile.IntersectsWith))continue;
            if(Mean(before,truth,tile,size.Width)>2||Mean(after,truth,tile,size.Width)>2)live++;
            double d=Math.Min(Mean(shown,truth,tile,size.Width),Math.Min(Mean(shown,before,tile,size.Width),Mean(shown,after,tile,size.Width))),j=Mean(ideal,truth,tile,size.Width);
            checkedTiles++;sumShown+=d;sumIdeal+=j;
            double excess=d-(2*j+8);if(excess>worstExcess){worstExcess=excess;worstTile=tile;worst=$"{tile.Location} viewer {d:F1} vs ideal JPEG {j:F1}";}
            if(excess>0)bad++;
        }
        if(bad>0)foreach(var (name,pixels) in new[]{("viewer",shown),("before",before),("truth",truth),("after",after)})Crop(pixels,size,worstTile).Save(Path.Combine(AppContext.BaseDirectory,$"mismatch-{name}.png"));
        return $"{(bad==0?"PASS":"FAIL")} viewer image matches the host screen: {checkedTiles} tiles, {bad} beyond JPEG noise of every screen sample ({live} were animating); mean |diff| viewer {sumShown/Math.Max(1,checkedTiles):F2} vs ideal JPEG {sumIdeal/Math.Max(1,checkedTiles):F2} (of 765); worst {worst}";
        static double Mean(uint[] a,uint[] b,Rectangle r,int stride){long t=0;for(int y=r.Top;y<r.Bottom;y++)for(int x=r.Left;x<r.Right;x++){uint p=a[y*stride+x],q=b[y*stride+x];t+=Math.Abs((int)(p>>16&255)-(int)(q>>16&255))+Math.Abs((int)(p>>8&255)-(int)(q>>8&255))+Math.Abs((int)(p&255)-(int)(q&255));}return t/(double)(r.Width*r.Height);}
        static Bitmap Crop(uint[] pixels,Size size,Rectangle tile){var r=Rectangle.Intersect(Rectangle.Inflate(tile,48,48),new(Point.Empty,size));var image=new Bitmap(r.Width*3,r.Height*3);for(int y=0;y<r.Height*3;y++)for(int x=0;x<r.Width*3;x++)image.SetPixel(x,y,Color.FromArgb((int)(pixels[(r.Y+y/3)*size.Width+r.X+x/3]|0xFF000000)));return image;}
    }
    // The best any JPEG pipeline at the default quality could show: the exact screen, encoded and decoded.
    private static unsafe uint[] JpegRoundTrip(uint[] pixels,Size size)
    {
        fixed(uint* p=pixels){
            using var image=new Bitmap(size.Width,size.Height,size.Width*4,System.Drawing.Imaging.PixelFormat.Format32bppRgb,(nint)p);
            using var stream=new MemoryStream(ScreenCapture.EncodeJpeg(image,65));using var decoded=new Bitmap(stream);
            var result=new uint[pixels.Length];var data=decoded.LockBits(new(Point.Empty,size),System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try{for(int y=0;y<size.Height;y++)new Span<uint>((uint*)(data.Scan0+y*data.Stride),size.Width).CopyTo(result.AsSpan(y*size.Width));}finally{decoded.UnlockBits(data);}
            return result;
        }
    }
    private static unsafe uint[] GdiTruth(Size size)
    {
        var b=Screen0;using var dib=new Gdi.Dib(b.Width,b.Height);dib.CopyFromScreen(b);
        var truth=new uint[size.Width*size.Height];
        fixed(uint* t=truth)Resample.Bilinear((byte*)dib.Bits,dib.Stride,b.Width,b.Height,(byte*)t,size.Width*4,size.Width,size.Height);
        return truth;
    }
    private static string Stats(string label,List<double> values,string unit="ms"){
        if(values.Count==0)return $"{label} n=0";values.Sort();return $"{label} p50={values[values.Count/2]:F1}{unit} p95={values[Math.Min(values.Count-1,(int)(values.Count*.95))]:F1}{unit}";
    }
    private static async Task<string> HostStages(string workload)
    {
        using var capture=new ScreenCapture(Screen0);var grab=new List<double>();var encode=new List<double>();var bytes=new List<double>();
        long sent=capture.Capture();
        for(int i=0;i<45;i++){
            await Task.Delay(33);var watch=Stopwatch.StartNew();long sequence=capture.Capture();double captured=watch.Elapsed.TotalMilliseconds;
            watch.Restart();var update=capture.Changes(sent);sent=sequence;
            if(i>=5){grab.Add(captured);encode.Add(watch.Elapsed.TotalMilliseconds);bytes.Add(update.Jpeg.Length/1024d);}
        }
        return $"HOST {workload}: {Stats("capture+diff",grab)} {Stats("encode",encode)} {Stats("bytes",bytes," KB")}";
    }
    private static async Task<string> ViewerStages(bool large)
    {
        using var capture=new ScreenCapture(Screen0);
        using var form=new Form{ClientSize=new(1100,700),StartPosition=FormStartPosition.Manual,Location=new(Screen0.Right-1120,Screen0.Bottom-760),ShowInTaskbar=false};
        var screen=new RemoteScreen{Dock=DockStyle.Fill,BackColor=Color.Black};form.Controls.Add(screen);form.Show();
        // Record first, then replay, so the viewer window is not capturing itself.
        var recorded=new List<TileUpdate>();long sent=0;form.Opacity=0;
        for(int i=0;i<31;i++){await Task.Delay(33);long sequence=capture.Capture();recorded.Add(capture.Changes(sent));sent=sequence;}
        form.Opacity=1;screen.Apply(recorded[0]);screen.Refresh();
        var decode=new List<double>();var paint=new List<double>();var full=new List<double>();
        foreach(var update in recorded.Skip(1)){
            var watch=Stopwatch.StartNew();screen.Apply(update);decode.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();screen.InvalidateChanges();screen.Update();paint.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();screen.Refresh();full.Add(watch.Elapsed.TotalMilliseconds);
        }
        form.Close();
        return $"VIEWER {(large?"large":"small")}: {Stats("decode+paste",decode)} {Stats("paint changes",paint)} {Stats("paint whole 1100x700",full)}";
    }
    private static async Task<string> EndToEnd(string name,int oneWayMs,double mbps,bool large,Probe probe)
    {
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var desktop=new DesktopSource(Screen0);
        await using var host=new HostService(desktop,new NoInput(),(_,_)=>Task.FromResult(new Approval(true)));
        host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
        using var link=oneWayMs>0?new LinkEmulator(host.Invitation.Port,oneWayMs,mbps):null;
        var invitation=link is null?host.Invitation:host.Invitation with{Port=link.Port};
        await using var viewer=await ViewerSession.ConnectAsync(invitation,"Perf",ct.Token,true);
        using var screen=new RemoteScreen();
        long frames=0,bytes=0;bool measuring=false;var seen=new Dictionary<int,double>();
        void Received(int length){
            int value=probe.Read(screen,Screen0);double now=probe.Now;
            if(!Volatile.Read(ref measuring))return;
            frames++;bytes+=length;
            lock(seen)if(value>=0&&!seen.ContainsKey(value))seen[value]=now;
        }
        var read=Task.Run(()=>viewer.RunAsync(frame=>{screen.Apply(frame);Received(frame.Jpeg.Length);},_=>{},ct.Token,update=>{screen.Apply(update);Received(update.Jpeg.Length);}));
        await Task.Delay(1500,ct.Token);int firstCounter=probe.Counter;Volatile.Write(ref measuring,true);
        await Task.Delay(6000,ct.Token);Volatile.Write(ref measuring,false);int lastCounter=probe.Counter;
        ct.Cancel();try{await read;}catch(Exception ex)when(ex is OperationCanceledException or IOException or ObjectDisposedException){}
        var latency=new List<double>();int changes=lastCounter-firstCounter;
        lock(seen)foreach(var (value,when) in seen){double? set=probe.SetTime(value);if(set is not null&&value>firstCounter)latency.Add(when-set.Value);}
        return $"E2E {name} {(large?"large":"small")}: fps={frames/6d:F1} KB/frame={(frames==0?0:bytes/1024d/frames):F1} KB/s={bytes/1024d/6:F0} {Stats("change->decoded",latency)} states seen={latency.Count}/{changes}";
    }
    private sealed class NoInput:IInputSink{public void Apply(InputEvent input){}public void ReleaseAll(){}}

    // Four 40-px patches encode a 12-bit counter in saturated colors that survive JPEG.
    internal sealed class Probe:Form
    {
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly double[] setTimes=new double[4096];
        internal int Counter{get;private set;}
        internal double Now=>clock.Elapsed.TotalMilliseconds;
        internal Probe(){FormBorderStyle=FormBorderStyle.None;TopMost=true;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new(Screen0.X+120,Screen0.Y+120);ClientSize=new(160,40);DoubleBuffered=true;}
        internal void Next(){Counter++;Refresh();lock(setTimes)setTimes[Counter&4095]=Now;}
        internal double? SetTime(int value){lock(setTimes){double t=setTimes[value&4095];return t==0?null:t;}}
        protected override void OnPaint(PaintEventArgs e){for(int i=0;i<4;i++){int bits=Counter>>(i*3)&7;using var brush=new SolidBrush(Color.FromArgb((bits&1)*255,(bits>>1&1)*255,(bits>>2&1)*255));e.Graphics.FillRectangle(brush,i*40,0,40,40);}}
        internal int Read(RemoteScreen frame,Rectangle screen){
            int value=0,low=Counter&~4095;var size=frame.ImageSize;if(size.IsEmpty)return -1;
            for(int i=0;i<4;i++){
                var c=frame.Pixel((int)((Location.X-screen.X+20+i*40)*(double)size.Width/screen.Width),(int)((Location.Y-screen.Y+20)*(double)size.Height/screen.Height));
                value|=((c.R>127?1:0)|(c.G>127?2:0)|(c.B>127?4:0))<<(i*3);
            }
            return low|value;
        }
    }
    // Realistic large motion: a page of text that scrolls every tick.
    internal sealed class ScrollingDocument:Form
    {
        private int offset;
        internal ScrollingDocument(){FormBorderStyle=FormBorderStyle.None;TopMost=true;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new(Screen0.X+320,Screen0.Y+180);ClientSize=new(1000,640);BackColor=Color.White;DoubleBuffered=true;}
        internal void Advance(){offset+=3;Refresh();}
        protected override void OnPaint(PaintEventArgs e){
            for(int line=0;line<34;line++){int n=line+offset/19;TextRenderer.DrawText(e.Graphics,$"{n,5}  The quick brown fox jumps over the lazy dog; remote support should feel immediate. Line {n} of the document.",Font,new Point(8,line*19-offset%19),Color.FromArgb(30,30,30));}
        }
    }
    // Bottleneck link with an unbounded queue, like a bloated home router: serialization at the
    // link rate plus fixed one-way propagation delay in each direction.
    internal sealed class LinkEmulator:IDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource stop=new();
        internal int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        internal LinkEmulator(int target,int oneWayMs,double mbps){listener.Start();_=AcceptAsync(target,oneWayMs,mbps*125_000);}
        private async Task AcceptAsync(int target,int delay,double rate){
            try{while(true){
                var a=await listener.AcceptTcpClientAsync(stop.Token);var b=new TcpClient{NoDelay=true};await b.ConnectAsync(IPAddress.Loopback,target,stop.Token);a.NoDelay=true;
                _=PipeAsync(a,b,delay,rate);_=PipeAsync(b,a,delay,rate);
            }}catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        }
        private async Task PipeAsync(TcpClient fromClient,TcpClient toClient,int delay,double rate){
            var queue=Channel.CreateUnbounded<(byte[] Data,long Due)>();var from=fromClient.GetStream();var to=toClient.GetStream();
            var writer=Task.Run(async()=>{try{await foreach(var (data,due) in queue.Reader.ReadAllAsync(stop.Token)){var wait=Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(),due);if(wait>TimeSpan.Zero)await Task.Delay(wait,stop.Token);await to.WriteAsync(data,stop.Token);}}catch(Exception){}finally{toClient.Dispose();}});
            double free=0;var buffer=new byte[64*1024];
            try{while(true){
                int n=await from.ReadAsync(buffer,stop.Token);if(n==0)break;
                double now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;free=Math.Max(free,now)+n/rate;
                queue.Writer.TryWrite((buffer[..n],(long)((free+delay/1000d)*Stopwatch.Frequency)));
            }}catch(Exception){}finally{queue.Writer.TryComplete();}
            await writer;
        }
        public void Dispose(){stop.Cancel();listener.Stop();}
    }
}
