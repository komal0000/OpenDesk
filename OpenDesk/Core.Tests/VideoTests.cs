using System.Net;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class VideoTests
{
    internal static void Register()
    {
        Test("Tile update round trip and bounds",async()=>{
            var update=new TileUpdate(1280,720,32,[0,3,919],[255,216,255,217],7);
            var parsed=TileUpdate.Unpack(update.Pack(new(true,4,"Connected")));
            Check(parsed.Update.Width==1280&&parsed.Update.Tiles.SequenceEqual(update.Tiles)&&parsed.Update.Jpeg.SequenceEqual(update.Jpeg)&&parsed.Status.Control&&parsed.Update.Epoch==4);
            Check(update.Atlas==(3,1)&&new TileUpdate(1280,720,32,new ushort[45],[255,216,255,217]).Atlas==(40,2));
            var empty=TileUpdate.Unpack(new TileUpdate(64,64,32,[],[]).Pack(new(false,2,""))).Update;Check(empty.Tiles.Length==0&&empty.Jpeg.Length==0);
            foreach(var bad in new[]{update with{Tiles=[920]},update with{Jpeg=[]},update with{TileSize=24},update with{Width=1601},new TileUpdate(64,64,32,[],[255,216,255,217])})
                await Throws<InvalidDataException>(()=>Task.FromResult(bad.Pack(new(false,0,""))));
            var packed=update.Pack(new(false,0,"")).Payload;packed[22]=0xFF;packed[21]=0xFF; // tile index beyond the grid
            await Throws<InvalidDataException>(()=>Task.FromResult(TileUpdate.Unpack(new(MessageKind.Tiles,packed))));
        });
        // A frame near the bandwidth-delay product is not queued behind another just to fill the link:
        // latency wins over the last fifth of throughput.
        foreach(var (mbps,rtt,kb,minimumFps) in new[]{(2d,500,85,2.2),(8d,500,85,9.4),(8d,500,3,55d),(100d,20,85,55d)})
            Test($"Pacer fills a {mbps} Mbps {rtt} ms link with {kb} KB frames without queueing stale video",()=>{
                var (fps,p95)=Simulate(mbps*125,rtt,kb*1024);double serialize=kb*1024/(mbps*125);
                double bound=rtt/2+2*serialize+.2*rtt+20;
                Console.WriteLine($"  sim {mbps} Mbps {rtt} ms {kb} KB: {fps:F1} fps (link max {mbps*125/(kb*1024)*1000:F1}), p95 {p95:F0} ms (bound {bound:F0}, ideal {rtt/2+serialize+3:F0})");
                Check(fps>=minimumFps,$"{fps:F1} fps, expected at least {minimumFps}");
                Check(p95<=bound,$"p95 capture-to-viewer {p95:F0} ms, bound {bound:F0} ms");
                return Task.CompletedTask;
            });
        Test("Tile viewers receive only changes; legacy viewers still get whole frames",async()=>{
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(8));var source=new FakeTiles();
            await using var host=new HostService(source,new RecordingInput(),(_,_)=>Task.FromResult(new Approval(true)));
            host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            await using var viewer=await ViewerSession.ConnectAsync(host.Invitation,"Tiles",ct.Token,true);
            var updates=new List<TileUpdate>();var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading=viewer.RunAsync(_=>throw new Exception("Tile viewer was sent a whole frame"),_=>{},ct.Token,u=>{lock(updates)updates.Add(u);first.TrySetResult();});
            await first.Task.WaitAsync(ct.Token);Check(viewer.Status.Tiles);
            await Task.Delay(300,ct.Token);lock(updates)Check(updates.Count==1&&updates[0].Tiles.Length==1,$"Unchanged screen was resent: {updates.Count} updates");
            Interlocked.Increment(ref source.Sequence);await Task.Delay(300,ct.Token);
            lock(updates)Check(updates.Count==2&&updates[1].Tiles.Length==1,$"{updates.Count} updates after one change");lock(source.Requests)Check(source.Requests.SequenceEqual([0L,1L]),"Second update was not a delta: "+string.Join(",",source.Requests));
            await using var legacy=await ViewerSession.ConnectAsync(host.Invitation,"Legacy",ct.Token);var whole=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var legacyReading=legacy.RunAsync(_=>whole.TrySetResult(),_=>{},ct.Token);await whole.Task.WaitAsync(ct.Token);Check(!legacy.Status.Tiles);
            host.Coordinator.SetController(null);host.Coordinator.ChangeDisplay(()=>{});await Task.Delay(300,ct.Token);
            lock(updates)Check(updates[^1].Tiles.Length==0&&updates[^1].Epoch==viewer.Status.Epoch&&viewer.Status.Epoch>0,"Epoch change was not delivered to an idle tile viewer");
            ct.Cancel();try{await Task.WhenAll(reading,legacyReading);}catch(OperationCanceledException){}
        });
    }
    // Bottleneck link with an unbounded queue; 60 Hz capture sends whenever the pacer allows.
    private static (double Fps,double P95) Simulate(double bytesPerMs,int rtt,int frameBytes)
    {
        var pacer=new FramePacer();var acks=new PriorityQueue<(double Receive,double Hold),double>();var latency=new List<double>();
        double linkFree=0;int frames=0;
        for(double now=0;now<30_000;now+=1){
            while(acks.TryPeek(out var ack,out var at)&&at<=now){acks.Dequeue();pacer.OnAck(at,ack.Receive,ack.Hold);}
            if(now%16!=0||!pacer.CanSend())continue;
            double start=Math.Max(linkFree,now);linkFree=start+frameBytes/bytesPerMs;
            double header=start+Math.Min(frameBytes,16*1024)/bytesPerMs+rtt/2d,complete=linkFree+rtt/2d;
            pacer.OnSend(frameBytes,now);acks.Enqueue((complete-header,complete+3-header),complete+3+rtt/2d);
            if(now>=10_000){frames++;latency.Add(complete-now);}
        }
        latency.Sort();return (frames/20d,latency[(int)(latency.Count*.95)]);
    }
    private sealed class FakeTiles:IScreenSource
    {
        public long Sequence=1;
        public readonly List<long> Requests=[];
        public long Capture()=>Volatile.Read(ref Sequence);
        public ScreenFrame Frame()=>new(8,8,[255,216,255,217]);
        public TileUpdate Changes(long since){lock(Requests)Requests.Add(since);long now=Volatile.Read(ref Sequence);return since>=now?new(64,64,32,[],[],now):new(64,64,32,[0],[255,216,255,217],now);}
    }
}
