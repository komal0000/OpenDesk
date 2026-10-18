using System.Net;
using System.Security.Cryptography;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class SessionToolsTests
{
    internal static void Register()
    {
        Test("Corrupted file data is rejected without publishing the destination",async()=>{
            string path=Path.Combine(Path.GetTempPath(),"opendesk-corrupt-"+Guid.NewGuid().ToString("N"));FileAck? last=null;
            await using var receiver=new FileTransfer((m,_)=>{if(m.Kind==MessageKind.FileAck)last=Wire.Decode<FileAck>(m);return Task.CompletedTask;},(_,_)=>Task.FromResult<string?>(path));
            var offer=new FileOffer(Guid.NewGuid(),"safe.bin",4,new string('0',64));
            await Throws<InvalidDataException>(()=>receiver.ReceiveAsync(Wire.Json(MessageKind.FileOffer,offer with{Length=FileTransfer.MaxBytes+1}),default));
            await receiver.ReceiveAsync(Wire.Json(MessageKind.FileOffer,offer),default);byte[] chunk=new byte[20];offer.Id.TryWriteBytes(chunk);chunk[16]=123;
            await receiver.ReceiveAsync(new(MessageKind.FileChunk,chunk),default);await receiver.ReceiveAsync(Wire.Json(MessageKind.FileFinish,offer.Id),default);
            Check(last is {Accepted:false,Stage:"done"}&&!File.Exists(path));
        });
        Test("Empty clipboard payload is rejected before a UI clipboard callback",async()=>{
            int received=0;await using var host=new HostService(()=>new(8,8,[255,216,255,217]),new RecordingInput(),(_,_)=>Task.FromResult(new Approval(true,false,true)));
            host.MessageReceived=(_,_,_)=>{received++;return Task.CompletedTask;};host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var tcp=new System.Net.Sockets.TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,host.Invitation.Port,ct.Token);
            using var ssl=await SecureSession.OpenClientAsync(tcp.GetStream(),host.Invitation,"Empty clipboard",ct.Token);await Wire.ReadAsync(ssl,ct.Token);
            await Wire.WriteAsync(ssl,Wire.Json(MessageKind.Clipboard,""),ct.Token);while(host.Coordinator.Viewers.Count!=0)await Task.Delay(10,ct.Token);Check(received==0);
        });
        Test("Raw peer cannot bypass host clipboard/file permission checks",async()=>{
            foreach(var kind in new[]{MessageKind.Clipboard,MessageKind.FileOffer}){
                int dispatched=0;await using var host=new HostService(()=>new(8,8,[255,216,255,217]),new RecordingInput(),(_,_)=>Task.FromResult(new Approval(true)));
                host.MessageReceived=(_,_,_)=>{dispatched++;return Task.CompletedTask;};host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
                using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var tcp=new System.Net.Sockets.TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,host.Invitation.Port,ct.Token);
                using var ssl=await SecureSession.OpenClientAsync(tcp.GetStream(),host.Invitation,"Bypass attempt",ct.Token);await Wire.ReadAsync(ssl,ct.Token);
                var message=kind==MessageKind.Clipboard?Wire.Json(kind,"not approved"):Wire.Json(kind,new FileOffer(Guid.NewGuid(),"x.txt",0,new string('0',64)));
                await Wire.WriteAsync(ssl,message,ct.Token);while(host.Coordinator.Viewers.Count!=0)await Task.Delay(10,ct.Token);Check(dispatched==0);
            }
        });
        Test("Chat, clipboard and a hashed file cross an approved TLS session",async()=>{
            string folder=Path.Combine(Path.GetTempPath(),"opendesk-files-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            string source=Path.Combine(folder,"source.bin"),target=Path.Combine(folder,"received.bin");byte[] content=RandomNumberGenerator.GetBytes(160_000);await File.WriteAllBytesAsync(source,content);
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await using var host=new HostService(()=>new(8,8,[255,216,255,217]),new RecordingInput(),(_,_)=>Task.FromResult(new Approval(true,false,true,true)));
            host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            FileTransfer? hostFiles=null;var chat=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var clipboard=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.MessageReceived=async(id,m,token)=>{
                if(m.Kind==MessageKind.Chat)chat.TrySetResult(Wire.Decode<string>(m));
                else if(m.Kind==MessageKind.Clipboard)clipboard.TrySetResult(Wire.Decode<string>(m));
                else {hostFiles??=new FileTransfer((message,t)=>host.SendMessageAsync(id,message,t),(_,_)=>Task.FromResult<string?>(target));await hostFiles.ReceiveAsync(m,token);}
            };
            await using var viewer=await ViewerSession.ConnectAsync(host.Invitation,"Tools",ct.Token);
            await using var sender=new FileTransfer((m,t)=>viewer.SendMessageAsync(m,t),(_,_)=>Task.FromResult<string?>(null));viewer.MessageReceived=sender.ReceiveAsync;
            var reading=viewer.RunAsync(_=>{},_=>{},ct.Token);
            await viewer.SendMessageAsync(Wire.Json(MessageKind.Chat,"hello"),ct.Token);await viewer.SendMessageAsync(Wire.Json(MessageKind.Clipboard,"shared text"),ct.Token);
            Check(await chat.Task.WaitAsync(ct.Token)=="hello"&&await clipboard.Task.WaitAsync(ct.Token)=="shared text");
            await sender.SendAsync(source,ct.Token);byte[] received=await File.ReadAllBytesAsync(target);Check(File.Exists(target)&&content.SequenceEqual(received));
            Check(viewer.Status.Files&&viewer.Status.Clipboard,"Frame metadata must preserve approved capabilities");
            await viewer.DisposeAsync();try{await reading;}catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
            if(hostFiles is not null)await hostFiles.DisposeAsync();File.Delete(source);File.Delete(target);
        });
        Test("Unapproved clipboard/files are rejected and display changes revoke stale input",async()=>{
            var sink=new RecordingInput();var co=new HostCoordinator(sink);var id=co.Add("A",true);long before=co.Status(id).Epoch;bool changed=false;co.ChangeDisplay(()=>changed=true);
            Check(changed&&co.Status(id).Epoch>before&&!co.HandleInput(id,new("key",Code:65,Epoch:before)));
            await using var host=new HostService(()=>new(8,8,[255,216,255,217]),new RecordingInput(),(_,_)=>Task.FromResult(new Approval(true)));host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            await using var viewer=await ViewerSession.ConnectAsync(host.Invitation,"No data permissions",default);
            await Throws<UnauthorizedAccessException>(()=>viewer.SendMessageAsync(Wire.Json(MessageKind.Clipboard,"no"),default));
            await Throws<UnauthorizedAccessException>(()=>viewer.SendMessageAsync(Wire.Json(MessageKind.FileOffer,new FileOffer(Guid.NewGuid(),"x",1,new string('0',64))),default));
        });
        Test("File receiver rejects traversal and removes incomplete data on disconnect",async()=>{
            string path=Path.Combine(Path.GetTempPath(),"opendesk-incomplete-"+Guid.NewGuid().ToString("N"));
            var offer=new FileOffer(Guid.NewGuid(),"valid.txt",100,new string('0',64));
            var receive=new FileTransfer((_,_)=>Task.CompletedTask,(_,_)=>Task.FromResult<string?>(path));
            await Throws<InvalidDataException>(()=>receive.ReceiveAsync(Wire.Json(MessageKind.FileOffer,offer with{Name="../bad"}),default));
            await receive.ReceiveAsync(Wire.Json(MessageKind.FileOffer,offer),default);await receive.DisposeAsync();
            Check(!File.Exists(path)&&!Directory.EnumerateFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".*.opendesk-part").Any());
        });
        Test("MJPEG recording has valid RIFF sizes, stream header and frame index",()=>{
            string path=Path.Combine(Path.GetTempPath(),"opendesk-recording-"+Guid.NewGuid().ToString("N")+".avi");
            using(var recording=new MjpegRecording(path,8,8)){recording.Add(new(8,8,[255,216,255,217]));recording.Add(new(8,8,[255,216,255,217]));}
            byte[] bytes=File.ReadAllBytes(path);Check(System.Text.Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&BitConverter.ToInt32(bytes,4)==bytes.Length-8);
            string text=System.Text.Encoding.ASCII.GetString(bytes);Check(text.Contains("MJPG")&&text.Contains("movi")&&text.Contains("idx1"));int index=text.IndexOf("idx1",StringComparison.Ordinal);Check(BitConverter.ToInt32(bytes,index+4)==32);File.Delete(path);return Task.CompletedTask;
        });
    }
}
