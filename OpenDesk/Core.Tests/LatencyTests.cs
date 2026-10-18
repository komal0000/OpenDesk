using System.Net;
using System.Net.Sockets;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class LatencyTests
{
    internal static void Register()
    {
        Test("Mouse motion coalesces without losing click order",async()=>{
            var queue=new InputQueue();
            for(int i=0;i<1000;i++)Check(queue.Post(new("move",X:i/1000d)));
            Check(queue.Post(new("button",X:.9,Y:.7,Code:1,Down:true)));
            for(int i=0;i<1000;i++)Check(queue.Post(new("move",X:.5,Y:i/1000d)));
            Check(queue.Post(new("button",X:.5,Y:.9,Code:1,Down:false)));queue.Complete();
            var events=new List<InputEvent>();await foreach(var input in queue.ReadAllAsync(default))events.Add(input);
            Check(events.Count==4&&events[0].Kind=="move"&&events[0].X==.999);
            Check(events[1].Down&&events[1].X==.9&&events[2].Y==.999&&!events[3].Down);
        });
        Test("New session bounds video in flight while allowing control",async()=>{
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var sink=new Sink();int sequence=0;
            await using var host=new HostService(()=>new(8,8,[255,216,(byte)Interlocked.Increment(ref sequence),217]),sink,(_,_)=>Task.FromResult(new Approval(true,true)));
            host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            using var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync(IPAddress.Loopback,host.Invitation.Port,ct.Token);
            using var ssl=await SecureSession.OpenClientAsync(tcp.GetStream(),host.Invitation,"Video window",ct.Token,true);
            var status=Wire.Decode<SessionStatus>(await Wire.ReadAsync(ssl,ct.Token));Check(status.FrameAcks&&status.Control);
            for(int i=0;i<2;i++)Check((await Wire.ReadAsync(ssl,ct.Token)).Kind==MessageKind.Frame);
            var next=Wire.ReadAsync(ssl,ct.Token);await Task.Delay(180,ct.Token);Check(!next.IsCompleted,"Video was allowed to build up beyond two frames");
            await Wire.WriteAsync(ssl,Wire.Json(MessageKind.Input,new InputEvent("button",.2,.3,1,true,status.Epoch)),ct.Token);
            await sink.Applied.Task.WaitAsync(ct.Token);
            await Wire.WriteAsync(ssl,new(MessageKind.FrameAck,[]),ct.Token);
            Check((await next).Kind==MessageKind.Frame);
        });
        Test("Legacy viewer remains compatible without video acknowledgments",async()=>{
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var host=new HostService(()=>new(8,8,[255,216,255,217]),new Sink(),(_,_)=>Task.FromResult(new Approval(true)));
            host.StartDirect("127.0.0.1",0,IPAddress.Loopback);using var tcp=new TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,host.Invitation.Port,ct.Token);
            using var ssl=await SecureSession.OpenClientAsync(tcp.GetStream(),host.Invitation,"Legacy",ct.Token);
            Check(!Wire.Decode<SessionStatus>(await Wire.ReadAsync(ssl,ct.Token)).FrameAcks);
            for(int i=0;i<4;i++)Check((await Wire.ReadAsync(ssl,ct.Token)).Kind==MessageKind.Frame);
        });
        Test("Idle cached capture still supplies a new viewer immediately",async()=>{
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));var cached=new ScreenFrame(8,8,[255,216,255,217]);
            await using var host=new HostService(()=>cached,new Sink(),(_,_)=>Task.FromResult(new Approval(true)));
            host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            await using var first=await ViewerSession.ConnectAsync(host.Invitation,"First",ct.Token);
            var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var running=first.RunAsync(_=>received.TrySetResult(),_=>{},ct.Token);await received.Task.WaitAsync(ct.Token);
            await using var second=await ViewerSession.ConnectAsync(host.Invitation,"Second",ct.Token);var secondFrame=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read=second.RunAsync(_=>secondFrame.TrySetResult(),_=>{},ct.Token);await secondFrame.Task.WaitAsync(TimeSpan.FromMilliseconds(700),ct.Token);
            ct.Cancel();try{await Task.WhenAll(running,read);}catch(OperationCanceledException){}
        });
        Test("Loopback video transport exceeds the old ten-fps ceiling",async()=>{
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var host=new HostService(()=>new(8,8,[255,216,255,217]),new Sink(),(_,_)=>Task.FromResult(new Approval(true)));
            host.StartDirect("127.0.0.1",0,IPAddress.Loopback);await using var viewer=await ViewerSession.ConnectAsync(host.Invitation,"Frame pacing",ct.Token);
            int count=0;var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var read=viewer.RunAsync(_=>{Interlocked.Increment(ref count);first.TrySetResult();},_=>{},ct.Token);
            await first.Task.WaitAsync(ct.Token);int start=Volatile.Read(ref count);await Task.Delay(1100,ct.Token);int delivered=Volatile.Read(ref count)-start;
            Check(delivered>=22,$"Only {delivered} frames in 1.1 seconds");ct.Cancel();try{await read;}catch(OperationCanceledException){}
        });
    }
    private sealed class Sink:IInputSink{public TaskCompletionSource Applied{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public void Apply(InputEvent input)=>Applied.TrySetResult();public void ReleaseAll(){}}
}
