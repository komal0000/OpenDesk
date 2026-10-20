using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class ServiceSettingsForm : Form
{
    private readonly ClientSettings settings;
    private readonly TextBox url = new() { Width=540 };
    private readonly TextBox pin = new() { Width=540 };
    private readonly TextBox name = new() { Width=300, MaxLength=64 };
    internal ServiceSettingsForm(ClientSettings settings)
    {
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        this.settings=settings; Text="OpenDesk · Network settings"; ClientSize=new(660,580);MinimumSize=new(560,480); StartPosition=FormStartPosition.CenterParent;
        Font=new Font("Segoe UI",10); var panel=Theme.Stack();
        panel.Controls.Add(Theme.Label("Device network",19,true));
        var explanation=Theme.Label("Both computers use the same OpenDesk service. A public server is needed for internet connections.\nConfigure the service once; normal connections use device numbers."); explanation.MaximumSize=new(555,0); panel.Controls.Add(explanation);
        panel.Controls.Add(Theme.Label("HTTPS service address")); url.Text=settings.ServiceUrl; panel.Controls.Add(url);
        panel.Controls.Add(Theme.Label("Service certificate fingerprint (for a self-hosted certificate)",9)); pin.Text=settings.ServicePin; panel.Controls.Add(pin);
        panel.Controls.Add(Theme.Label("Your display name")); name.Text=settings.DisplayName; panel.Controls.Add(name);
        var save=Theme.Button("Save settings",true); var local=Theme.Button("Start local service");
        save.Click+=(_,_)=>Save(); local.Click+=async(_,_)=>await StartLocalAsync(local);
        var stop=Theme.Button("Stop local service");stop.Click+=async(_,_)=>{try{string exe=Path.Combine(AppContext.BaseDirectory,"OpenDesk.Service.exe");using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,"--stop"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden});if(process is not null)await process.WaitForExitAsync();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Local service");}};
        panel.Controls.Add(Theme.Row(save,local,stop));
        var hint=Theme.Label("For another PC, use this service machine's IP/hostname instead of localhost, and the same fingerprint.\nThe service uses HTTPS 47834 and relay TCP 47832 by default.",9); hint.MaximumSize=new(555,0); panel.Controls.Add(hint); Controls.Add(panel);
    }
    private void Save()
    {
        try {
            string fingerprint=pin.Text.Replace(" ","").Trim().ToUpperInvariant();using var check=new DirectoryClient(url.Text.Trim(),fingerprint);
            if (string.IsNullOrWhiteSpace(name.Text)) throw new FormatException("Enter your display name.");
            string normalized=check.Endpoint.ToString();
            if(settings.ServiceUrl!=normalized || settings.ServicePin!=fingerprint) settings.DeviceId="";
            settings.ServiceUrl=normalized; settings.ServicePin=fingerprint; settings.DisplayName=name.Text.Trim(); settings.Save(); DialogResult=DialogResult.OK; Close();
        } catch(Exception ex) { MessageBox.Show(this,ex.Message,"Settings",MessageBoxButtons.OK,MessageBoxIcon.Information); }
    }
    private async Task StartLocalAsync(Button button)
    {
        button.Enabled=false;
        try {
            string executable=Path.Combine(AppContext.BaseDirectory,"OpenDesk.Service.exe");
            if(!File.Exists(executable)) throw new IOException("Put OpenDesk.Service.exe beside OpenDesk.exe, or run the service package separately.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute=true, WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden, WorkingDirectory=AppContext.BaseDirectory });
            for(int i=0;i<30;i++) {
                await Task.Delay(300);
                if(IsDisposed)return;
                if(!settings.TryLocalService()) continue;
                // Ready means it answers the v2 signaling check, so an old instance still running is not accepted.
                try { using var check=new DirectoryClient(settings.ServiceUrl,settings.ServicePin); if(await check.SupportsSignalingAsync(default)){url.Text=settings.ServiceUrl; pin.Text=settings.ServicePin; return;} }
                catch(Exception ex) when (ex is HttpRequestException or IOException) { }
            }
            throw new IOException("The service did not become ready. Check whether its ports are already in use.");
        } catch(Exception ex) { if(!IsDisposed)MessageBox.Show(this,ex.Message,"Local service",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        finally { if(!IsDisposed)button.Enabled=true; }
    }
}
