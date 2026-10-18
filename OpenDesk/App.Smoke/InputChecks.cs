using System.Diagnostics;
using System.Net;
using System.Text.Json;
using OpenDesk.App;
using OpenDesk.Core;

namespace OpenDesk.Smoke;

internal static class InputChecks
{
    internal sealed record TargetState(int X,int Y,int Clicks,int Characters);
    internal static void RunTarget(string path){
        using var form=new Form{Text="OpenDesk input verification",ClientSize=new(380,200),StartPosition=FormStartPosition.CenterScreen,TopMost=true};
        var button=new Button{Text="Temporary click target",Dock=DockStyle.Top,Height=110};var text=new TextBox{Dock=DockStyle.Bottom};form.Controls.Add(text);form.Controls.Add(button);int clicks=0;
        void Save(){var p=button.PointToScreen(new Point(button.Width/2,button.Height/2));File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new TargetState(p.X,p.Y,clicks,text.TextLength)));File.Move(path+".tmp",path,true);}
        button.Click+=(_,_)=>{clicks++;button.Text=$"Received {clicks} clicks";text.Focus();Save();};text.TextChanged+=(_,_)=>Save();form.Shown+=(_,_)=>Save();Application.Run(form);
    }
    internal static async Task RunAsync(bool publicRelay){
        string stateFile=Path.Combine(Path.GetTempPath(),"opendesk-input-"+Guid.NewGuid().ToString("N")+".json");
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};start.ArgumentList.Add("--input-target");start.ArgumentList.Add(stateFile);
        using var target=Process.Start(start)!;Point original=Cursor.Position;
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var bounds=Screen.PrimaryScreen!.Bounds;using var capture=new ScreenCapture(bounds);
        await using var host=new HostService(capture.Capture,new WindowsInput(bounds),(_,_)=>Task.FromResult(new Approval(true,true)));
        ViewerSession? session=null;Task? reading=null;
        try{
            if(publicRelay)await host.StartRelayAsync("139.99.135.69",47832);else host.StartDirect("127.0.0.1",0,IPAddress.Loopback);
            session=await ViewerSession.ConnectAsync(host.Invitation,"OpenDesk input verification",ct.Token);
            int frames=0;reading=session.RunAsync(_=>Interlocked.Increment(ref frames),_=>{},ct.Token);
            TargetState? state;while((state=Read(stateFile))is null)await Task.Delay(20,ct.Token);
            var delays=new List<double>();int count=publicRelay?8:20;
            for(int i=0;i<count;i++){
                double x=(state.X-bounds.Left)/(double)(bounds.Width-1),y=(state.Y-bounds.Top)/(double)(bounds.Height-1);var watch=Stopwatch.StartNew();
                await session.SendInputAsync(new("button",x,y,1,true,session.Status.Epoch),ct.Token);
                await session.SendInputAsync(new("button",x,y,1,false,session.Status.Epoch),ct.Token);
                while((Read(stateFile)?.Clicks??0)<i+1)await Task.Delay(5,ct.Token);delays.Add(watch.Elapsed.TotalMilliseconds);
                await Task.Delay(70,ct.Token);
            }
            for(int i=0;i<5;i++){await session.SendInputAsync(new("key",Code:0x41,Down:true,Epoch:session.Status.Epoch),ct.Token);await session.SendInputAsync(new("key",Code:0x41,Down:false,Epoch:session.Status.Epoch),ct.Token);}
            while((Read(stateFile)?.Characters??0)<5)await Task.Delay(5,ct.Token);
            if(Read(stateFile) is not {Clicks:var actual,Characters:5}||actual!=count)throw new Exception("Windows click/keyboard count mismatch");
            string samples=string.Join(", ",delays.Select(d=>d.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)));delays.Sort();File.WriteAllText(Path.Combine(AppContext.BaseDirectory,publicRelay?"input-public.txt":"input-local.txt"),$"PASS {(publicRelay?"public VPS relay":"loopback TLS")} -> actual Windows SendInput -> separate test window with consent-scope input filter: {count}/{count} clicks, 5/5 characters, {frames} real screen frames; click delivery p50={delays[delays.Count/2]:F1} ms p95={delays[(int)Math.Ceiling(delays.Count*.95)-1]:F1} ms max={delays[^1]:F1} ms. Samples: {samples}. Cursor restored; target closed.");
        }catch(Exception ex){throw new Exception($"Input probe failed; target={JsonSerializer.Serialize(Read(stateFile))}; session control={session?.Status.Control}; reader={reading?.Status}",ex);}finally{
            ct.Cancel();if(session is not null)await session.DisposeAsync();if(reading is not null)try{await reading;}catch(OperationCanceledException){}catch(IOException){}
            if(!target.HasExited){target.CloseMainWindow();if(!target.WaitForExit(1500))target.Kill();}Cursor.Position=original;
            if(File.Exists(stateFile))File.Delete(stateFile);if(File.Exists(stateFile+".tmp"))File.Delete(stateFile+".tmp");
        }
    }
    private static TargetState? Read(string path){try{return JsonSerializer.Deserialize<TargetState>(File.ReadAllText(path));}catch(IOException){return null;}catch(JsonException){return null;}}
}
