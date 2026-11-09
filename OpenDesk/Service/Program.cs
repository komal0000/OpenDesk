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
string? turnUrl=null,turnSecretFile=null;
for (int i=0;i<args.Length;i++) {
    if (args[i] == "--data" && i+1<args.Length) data = Path.GetFullPath(args[++i]);
    else if (args[i] == "--port" && i+1<args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--relay-port" && i+1<args.Length) relayPort = int.Parse(args[++i]);
    else if(args[i]=="--loopback")loopback=true;
    else if(args[i]=="--turn-url"&&i+1<args.Length)turnUrl=args[++i];
    else if(args[i]=="--turn-secret-file"&&i+1<args.Length)turnSecretFile=Path.GetFullPath(args[++i]);
    else throw new ArgumentException("Options: --data folder --port 47834 --relay-port 47832 [--turn-url host:3478 --turn-secret-file path]");
}
if (port is <1024 or >65535 || relayPort is <1024 or >65535 || port == relayPort) throw new ArgumentException("Choose two different ports between 1024 and 65535.");
// TURN (coturn with use-auth-secret) lets v2 sessions cross networks where a direct path fails.
TurnSettings? turn=null;
if(turnUrl is not null||turnSecretFile is not null){
    int colon=turnUrl?.LastIndexOf(':')??-1;
    if(turnUrl is null||turnSecretFile is null||colon<1||!int.TryParse(turnUrl[(colon+1)..],out int turnPort)||turnPort is <1 or >65535||Uri.CheckHostName(turnUrl[..colon])==UriHostNameType.Unknown)
        throw new ArgumentException("Use --turn-url host:port together with --turn-secret-file.");
    string secret=File.ReadAllText(turnSecretFile).Trim();
    if(secret.Length<32)throw new ArgumentException("The TURN secret must be at least 32 characters.");
    turn=new(turnUrl[..colon],turnPort,secret);
}
using var identity = ServiceIdentity.Load(data);
await using var server = await DirectoryHost.StartAsync(Path.Combine(data,"devices.json"),identity,port,relayPort,loopback,turn);
Console.WriteLine(turn is null ? "TURN not configured: v2 sessions connect directly only (same network)." : $"TURN: {turn.Host}:{turn.Port}");
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
