using OpenDesk.Core;
using OpenDesk.Service;

if(args.Contains("--stop")){
    int option=Array.IndexOf(args,"--port"),controlPort=option>=0&&option+1<args.Length?int.Parse(args[option+1]):47834;
    if(controlPort is <1024 or >65535)throw new ArgumentException("Invalid service port.");
    using var control=new System.IO.Pipes.NamedPipeClientStream(".","OpenDesk.Service."+controlPort,System.IO.Pipes.PipeDirection.Out,System.IO.Pipes.PipeOptions.Asynchronous);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));await control.ConnectAsync(timeout.Token);await control.WriteAsync(new byte[]{1},timeout.Token);return;
}
if (args.Contains("--smoke")) {
    using var cert = SecureSession.CreateCertificate();
    string folder = Path.Combine(Path.GetTempPath(),"opendesk-service-smoke-" + Guid.NewGuid().ToString("N"));
    await using var service = await DirectoryHost.StartAsync(Path.Combine(folder,"devices.json"),cert,0,0,true);
    using var client = new DirectoryClient(service.Url,SecureSession.Pin(cert));
    var info = await client.InfoAsync(default);
    if (info.RelayPort != service.RelayPort) throw new Exception("Service route mismatch");
    Console.WriteLine("PASS HTTPS device service and relay startup/shutdown"); return;
}
string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenDeskServer");
int port = 47834, relayPort = 47832;
bool loopback=false;
for (int i=0;i<args.Length;i++) {
    if (args[i] == "--data" && i+1<args.Length) data = Path.GetFullPath(args[++i]);
    else if (args[i] == "--port" && i+1<args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--relay-port" && i+1<args.Length) relayPort = int.Parse(args[++i]);
    else if(args[i]=="--loopback")loopback=true;
    else throw new ArgumentException("Options: --data folder --port 47834 --relay-port 47832");
}
if (port is <1024 or >65535 || relayPort is <1024 or >65535 || port == relayPort) throw new ArgumentException("Choose two different ports between 1024 and 65535.");
using var identity = ServiceIdentity.Load(data);
await using var server = await DirectoryHost.StartAsync(Path.Combine(data,"devices.json"),identity,port,relayPort,loopback);
ServiceIdentity.WritePublicConfig(data,port,SecureSession.Pin(identity));
Console.WriteLine($"OpenDesk device service: HTTPS {port}, relay TCP {relayPort}");
Console.WriteLine("Service certificate fingerprint: " + SecureSession.Pin(identity));
Console.WriteLine("Configure clients with this machine's hostname/IP and fingerprint. Press Ctrl+C to stop.");
var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_,e) => { e.Cancel=true; exit.TrySetResult(); };
using var controlStop=new CancellationTokenSource();
var pipe=Task.Run(async()=>{
    try{using var control=new System.IO.Pipes.NamedPipeServerStream("OpenDesk.Service."+port,System.IO.Pipes.PipeDirection.In,1,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous|System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await control.WaitForConnectionAsync(controlStop.Token);byte[] value=new byte[1];await control.ReadExactlyAsync(value,controlStop.Token);if(value[0]==1)exit.TrySetResult();
    }catch(OperationCanceledException){}
});
await exit.Task;
controlStop.Cancel();await pipe;
