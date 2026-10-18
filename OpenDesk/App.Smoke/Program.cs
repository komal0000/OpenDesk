using System.Security.Cryptography;
using OpenDesk.App;
using OpenDesk.Core;
using OpenDesk.Service;

namespace OpenDesk.Smoke;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length==2&&args[0]=="--input-target"){Application.AddMessageFilter(new RemoteInputFilter());InputChecks.RunTarget(args[1]);return;}
        Application.AddMessageFilter(new RemoteInputFilter());
        if(!RemoteInputFilter.Blocks(0x201,RemoteInputFilter.Marker)||RemoteInputFilter.Blocks(0x201,0))throw new Exception("Input filter mismatch");
        using var root=new Form{Opacity=0,ShowInTaskbar=false};
        root.Shown+=async(_,_)=>{
            if(args.Contains("--input-local")||args.Contains("--input-public")){
                try{await InputChecks.RunAsync(args.Contains("--input-public"));}catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"input-failure.txt"),ex.ToString());Environment.ExitCode=1;}finally{root.Close();}return;
            }
            if(args.Contains("--quality")){
                try{await QualityChecks.RunAsync();}catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"quality-failure.txt"),ex.ToString());Environment.ExitCode=1;}finally{root.Close();}return;
            }
            DirectoryHost? service=null;MainForm? host=null,viewerHome=null;ViewerForm? viewer=null;
            using var cert=SecureSession.CreateCertificate();
            string identity="OpenDesk.Smoke."+Guid.NewGuid().ToString("N"),other=identity+".viewer";
            string folder=Path.Combine(Path.GetTempPath(),identity);Directory.CreateDirectory(folder);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{
                const string serviceUrlForTest="https://saved.example:47834";
                string defaultsFile=Path.Combine(folder,"defaults.json");
                var defaults=ClientSettings.Load(defaultsFile);
                if(defaults.ServiceUrl!=ClientSettings.DefaultServiceUrl||defaults.ServicePin!=ClientSettings.DefaultServicePin)throw new Exception("Fresh profile did not use public service defaults");
                File.WriteAllText(defaultsFile,"{\"ServiceUrl\":\"\",\"ServicePin\":\"\",\"DeviceId\":\"123456789\"}");
                defaults=ClientSettings.Load(defaultsFile);
                if(defaults.ServiceUrl!=ClientSettings.DefaultServiceUrl||defaults.ServicePin!=ClientSettings.DefaultServicePin||defaults.DeviceId.Length!=0)throw new Exception("Empty-service migration mismatch");
                defaults.ServiceUrl=serviceUrlForTest;defaults.ServicePin=new string('A',64);defaults.Save();
                defaults=ClientSettings.Load(defaultsFile);
                if(defaults.ServiceUrl!=serviceUrlForTest||defaults.ServicePin!=new string('A',64))throw new Exception("Saved service settings were overwritten");
                service=await DirectoryHost.StartAsync(Path.Combine(folder,"devices.json"),cert,0,0,true);
                using(var a=ClientSettings.LoadDeviceKey(identity))using(var b=ClientSettings.LoadDeviceKey(identity)){
                    if(!a.ExportSubjectPublicKeyInfo().SequenceEqual(b.ExportSubjectPublicKeyInfo()))throw new Exception("Device key did not survive reopen");
                }
                var hostSettings=new ClientSettings{ServiceUrl=service.Url,ServicePin=SecureSession.Pin(cert),DisplayName="Smoke host",StorageFile=Path.Combine(folder,"host.json")};
                var viewerSettings=new ClientSettings{ServiceUrl=service.Url,ServicePin=SecureSession.Pin(cert),DisplayName="Smoke viewer",StorageFile=Path.Combine(folder,"viewer.json")};
                var observed=new ObservedInput();host=new MainForm(true,hostSettings,()=>ClientSettings.LoadDeviceKey(identity),observed){Opacity=0,ShowInTaskbar=false};host.Show(root);
                viewerHome=new MainForm(true,viewerSettings,()=>ClientSettings.LoadDeviceKey(other)){Opacity=0,ShowInTaskbar=false};viewerHome.Show(root);
                while(!host.IsOnline||!viewerHome.IsOnline)await Task.Delay(30,timeout.Token);
                if(host.DeviceAddress==viewerHome.DeviceAddress)throw new Exception("Different devices received the same ID");
                var input=(TextBox)viewerHome.Controls.Find("RemoteAddress",true).Single();var button=(Button)viewerHome.Controls.Find("Connect",true).Single();
                input.Text=DeviceProof.FormatId(host.DeviceAddress);button.PerformClick();
                ApprovalForm? approval;
                while((approval=Application.OpenForms.OfType<ApprovalForm>().FirstOrDefault()) is null)await Task.Delay(10,timeout.Token);
                // Exercise the real approval button; no SendInput/OS input is generated.
                var allow=Descendants(approval).OfType<Button>().Single(b=>b.Text=="Allow control");
                var latency=System.Diagnostics.Stopwatch.StartNew();allow.PerformClick();
                while((viewer=Application.OpenForms.OfType<ViewerForm>().FirstOrDefault()) is null||viewer.RenderedFrames==0)await Task.Delay(10,timeout.Token);
                double firstFrame=latency.Elapsed.TotalMilliseconds;
                while(viewer.RenderedFrames<3)await Task.Delay(50,timeout.Token);
                if(!viewer.InputEnabled)throw new Exception("Host allowed control but viewer still required hidden activation");
                var surface=viewer.ScreenSurface;
                var down=typeof(Control).GetMethod("OnMouseDown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                var up=typeof(Control).GetMethod("OnMouseUp",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                for(int i=0;i<20;i++){var argsClick=new MouseEventArgs(MouseButtons.Left,1,surface.Width/2,surface.Height/2,0);down.Invoke(surface,[argsClick]);up.Invoke(surface,[argsClick]);}
                while(observed.Events.Count<40)await Task.Delay(10,timeout.Token);
                var events=observed.Events.ToArray();if(events.Length!=40||events.Where((e,i)=>e.Kind!="button"||e.Code!=1||e.Down!=(i%2==0)||Math.Abs(e.X-.5)>.01||Math.Abs(e.Y-.5)>.01).Any())throw new Exception("Viewer click forwarding mismatch");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(AppContext.BaseDirectory,"home-online-smoke.png"));}
                if(viewerSettings.Contacts.All(c=>c.Id!=host.DeviceAddress))throw new Exception("Recent session was not stored");
                using var capture=new ScreenCapture(Screen.PrimaryScreen!.Bounds);var frame=capture.Capture();
                using(var recording=new MjpegRecording(Path.Combine(AppContext.BaseDirectory,"recording-smoke.avi"),frame.Width,frame.Height)){for(int i=0;i<3;i++){recording.Add(frame);await Task.Delay(100);}}
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"session-smoke.txt"),$"PASS two actual Windows app homes -> distinct persistent numeric IDs -> HTTPS lookup -> Connect button -> real Allow control dialog -> relay -> pinned TLS -> capture -> viewer rendering; control active immediately; 20/20 viewer-surface clicks received in order at host; {viewer.RenderedFrames} frames; first frame {firstFrame:F0} ms after approval (target <1000); decode {viewer.LastDecodeMilliseconds:F1} ms off UI thread; recent session stored; no OS input injected in this test.");
                if(firstFrame>=1000)Environment.ExitCode=2;
            }catch(Exception ex){Environment.ExitCode=1;File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"session-smoke.txt"),"FAIL "+ex);}
            finally{
                if(host is not null)await host.GoOfflineAsync();if(viewerHome is not null)await viewerHome.GoOfflineAsync();
                viewer?.Close();host?.Close();viewerHome?.Close();if(service is not null)await service.DisposeAsync();
                foreach(string name in new[]{identity,other})if(CngKey.Exists(name)){using var key=CngKey.Open(name);key.Delete();}
                root.Close();
            }
        };
        Application.Run(root);
    }
    private static IEnumerable<Control> Descendants(Control root){foreach(Control c in root.Controls){yield return c;foreach(var child in Descendants(c))yield return child;}}
    private sealed class ObservedInput:IInputSink{internal System.Collections.Concurrent.ConcurrentQueue<InputEvent> Events{get;}=new();public void Apply(InputEvent value)=>Events.Enqueue(value);public void ReleaseAll(){}}
}
