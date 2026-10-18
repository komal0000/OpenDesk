using System.Security.Cryptography;
using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class MainForm : Form
{
    private readonly ClientSettings settings;
    private readonly Func<ECDsa> deviceKeyFactory;
    private readonly IInputSink? inputOverride;
    private readonly TextBox remote = new() { Width=290, Font=new Font("Segoe UI",15), PlaceholderText="Enter remote address", MaxLength=32 };
    private readonly Button connect = Theme.Button("Connect  →",true);
    private readonly Label deviceId = Theme.Label("Not registered",30,true), network = Theme.Label("Offline · configure your device network",10);
    private readonly Button online = Theme.Button("Go online",true), stopAll = Theme.Button("Stop All");
    private readonly ComboBox monitor = new() { Width=260, DropDownStyle=ComboBoxStyle.DropDownList };
    private readonly ComboBox quality = new() { Width=220,DropDownStyle=ComboBoxStyle.DropDownList };
    private readonly NumericUpDown limit = new() { Minimum=1,Maximum=10,Value=5,Width=60 };
    private readonly Panel content = new() { Dock=DockStyle.Fill,Padding=new Padding(30),BackColor=Theme.Paper };
    private readonly Label bottom = Theme.Label("Ready",9);
    private readonly ListView viewers = new() { View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,Dock=DockStyle.Fill,Height=180 };
    private readonly List<ViewerForm> windows=[];
    private readonly List<ApprovalForm> requests=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,FileTransfer> transfers=[];
    private readonly Dictionary<Guid,ChatForm> chats=[];
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> fileCleanup=[];
    private DesktopSource? desktop;
    private readonly Screen[] screens=Screen.AllScreens;
    private HostService? host;
    private DirectoryClient? directory;
    private ECDsa? deviceKey;
    private CancellationTokenSource? hosting,joining;
    private Task? registration;
    private bool closing;
    private bool connectingHost;
    private readonly bool autoConnect;
    private string currentPage="home";
    internal bool IsOnline=>host?.IsRunning==true&&settings.DeviceId.Length==9&&registration is not null;
    internal string DeviceAddress=>settings.DeviceId;
    internal Task GoOfflineAsync()=>StopAsync();

    public MainForm(bool autoConnect=true,ClientSettings? profile=null,Func<ECDsa>? keyFactory=null,IInputSink? inputOverride=null)
    {
        settings=profile??ClientSettings.Load();deviceKeyFactory=keyFactory??(()=>ClientSettings.LoadDeviceKey());this.inputOverride=inputOverride;
        this.autoConnect=autoConnect;
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;DoubleBuffered=true;
        Text="OpenDesk · New Session"; ClientSize=new(1160,790); MinimumSize=new(800,560); StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10); BackColor=Theme.Paper;
        var top=new TableLayoutPanel { Dock=DockStyle.Top,Height=76,BackColor=Color.White,Padding=new Padding(20,14,20,14),ColumnCount=4,RowCount=1 };
        top.ColumnStyles.Add(new(SizeType.Absolute,200));top.ColumnStyles.Add(new(SizeType.Absolute,128));top.ColumnStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.Absolute,128));top.RowStyles.Add(new(SizeType.Percent,100));
        var brand=Theme.Label("◇ OpenDesk",19,true); brand.ForeColor=Theme.Green;brand.Dock=DockStyle.Fill;brand.TextAlign=ContentAlignment.MiddleLeft;brand.Margin=Padding.Empty;
        var addressLabel=Theme.Label("Remote address",10,true);addressLabel.AutoSize=false;addressLabel.Dock=DockStyle.Fill;addressLabel.TextAlign=ContentAlignment.MiddleLeft;addressLabel.Margin=Padding.Empty;
        remote.Dock=DockStyle.Fill;remote.Margin=new(0,7,10,0);remote.AccessibleName="Remote address";connect.Dock=DockStyle.Fill;connect.AutoSize=false;connect.MinimumSize=Size.Empty;connect.Margin=new(0,3,0,3);
        top.Controls.Add(brand,0,0);top.Controls.Add(addressLabel,1,0);top.Controls.Add(remote,2,0);top.Controls.Add(connect,3,0);
        var side=new FlowLayoutPanel { Name="Navigation",Dock=DockStyle.Left,Width=228,BackColor=Color.FromArgb(239,239,241),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(12,26,12,8) };
        foreach(var item in new[] { ("＋  New session", "home"),("◷  Recent sessions","recent"),("☆  Favorites","favorites"),("▣  Address book","contacts"),("⇄  Incoming sessions","incoming"),("⚙  Settings","settings") }) {
            var nav=Theme.Button(item.Item1);nav.Name="Nav-"+item.Item2;nav.AutoSize=false;nav.Size=new(204,44);nav.MinimumSize=Size.Empty;nav.Margin=new(0,4,0,4);nav.Padding=new(8,0,0,0); nav.TextAlign=ContentAlignment.MiddleLeft; nav.FlatAppearance.BorderSize=0; nav.BackColor=side.BackColor;
            string page=item.Item2; nav.Click+=(_,_)=> { if(page=="settings") Configure(); else ShowPage(page); }; side.Controls.Add(nav);
        }
        var footer=new Panel { Dock=DockStyle.Bottom,Height=32,BackColor=Color.White,Padding=new Padding(16,0,0,0) }; footer.Controls.Add(bottom);
        Controls.Add(content); Controls.Add(side); Controls.Add(footer); Controls.Add(top);
        for(int i=0;i<screens.Length;i++) monitor.Items.Add($"Display {i+1} · {screens[i].Bounds.Width} × {screens[i].Bounds.Height}");
        monitor.SelectedIndex=Math.Max(0,Array.FindIndex(screens,s=>s.Primary));
        quality.Items.AddRange(["Fast · 1024 px","Balanced · 1280 px","Sharp · 1600 px"]);quality.SelectedIndex=Math.Clamp(settings.VideoQuality,0,2);
        monitor.SelectedIndexChanged+=(_,_)=>UpdateCapture();quality.SelectedIndexChanged+=(_,_)=>{settings.VideoQuality=quality.SelectedIndex;settings.Save();UpdateCapture();};
        viewers.Columns.Add("Connected viewer",340); viewers.Columns.Add("Permission",180);
        connect.Click+=async(_,_)=>await ConnectAsync(); remote.KeyDown+=async(_,e)=> { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; await ConnectAsync(); } };
        remote.Name="RemoteAddress";connect.Name="Connect";
        online.Click+=async(_,_)=>await GoOnlineAsync(); stopAll.Click+=async(_,_)=>await StopAsync(); stopAll.Enabled=false;
        deviceId.Text=settings.DeviceId.Length==9 ? DeviceProof.FormatId(settings.DeviceId) : "Not registered";
        ShowPage("home");
        Shown+=async(_,_)=> { if(this.autoConnect && settings.ServiceUrl.Length>0) await GoOnlineAsync(); };
        FormClosing+=async(_,e)=> {
            if(closing) return; e.Cancel=true; closing=true; Enabled=false; joining?.Cancel();
            foreach(var window in windows.ToArray()) window.Close(); await StopAsync(); Close();
        };
    }
    private Panel Page(string title,string subtitle)
    {
        foreach(var shared in new Control[]{deviceId,network,online,stopAll,monitor,quality,limit,viewers})shared.Parent?.Controls.Remove(shared);
        foreach(Control old in content.Controls.Cast<Control>().ToArray()){content.Controls.Remove(old);old.Dispose();}Text="OpenDesk · " + title;
        var panel=Theme.Stack(); panel.Padding=Padding.Empty; panel.BackColor=Theme.Paper;
        panel.Controls.Add(Theme.Label(title,22,true)); var desc=Theme.Label(subtitle); desc.MaximumSize=new(880,0); panel.Controls.Add(desc);
        content.Controls.Add(panel); return panel;
    }
    private void ShowPage(string page)
    {
        currentPage=page;
        if(page=="home") {
            var panel=Page("New Session","Share your address, or enter a remote address above to connect.");
            var card=Theme.Stack();card.AutoScroll=false;card.AutoSize=true;card.AutoSizeMode=AutoSizeMode.GrowAndShrink;card.Padding=new(24,12,24,12);card.Margin=new(0,12,0,12);
            var title=Theme.Label("Your address",12,true);title.Margin=new(0,0,0,4);deviceId.Margin=new(0,0,0,4);network.Margin=new(0,2,0,6);
            var copy=Theme.Button("Copy address");copy.Click+=(_,_)=> { if(settings.DeviceId.Length==9) { try { Clipboard.SetText(settings.DeviceId); } catch(System.Runtime.InteropServices.ExternalException) { } } };
            var copyRow=Theme.Row(copy);copyRow.Margin=new(0,2,0,2);card.Controls.AddRange([title,deviceId,network,copyRow]); panel.Controls.Add(card);
            panel.Controls.Add(Theme.Row(online,stopAll));
            panel.Controls.Add(Theme.Label("Share this display",11,true));
            var preview=Theme.Button("Preview"); preview.Click+=(_,_)=>Preview(); panel.Controls.Add(Theme.Row(monitor,preview,Theme.Label("Viewer limit"),limit));
            panel.Controls.Add(Theme.Row(Theme.Label("Screen quality"),quality));
            panel.Controls.Add(Theme.Label("Recent sessions",13,true)); panel.Controls.Add(ContactTiles(settings.Contacts.Take(6)));
            var help=Theme.Label("Every incoming connection needs your approval. Multiple people can view; only one remote viewer controls at a time.",9); help.MaximumSize=new(850,0); panel.Controls.Add(help);
        }
        else if(page=="incoming") {
            var panel=Page("Incoming sessions","Approve requests, manage connected viewers, and choose who has control.");
            panel.Controls.Add(viewers); panel.Controls.Add(Theme.Row(online,stopAll));
            var grant=Theme.Button("Give control",true); var revoke=Theme.Button("Revoke control"); var remove=Theme.Button("Disconnect viewer");
            grant.Tag=revoke.Tag=remove.Tag="LocalPermission";
            grant.Click+=(_,_)=> { if(Selected() is Guid id) { try { host?.Coordinator.SetController(id); } catch(InvalidOperationException) { RefreshViewers(); } } };
            revoke.Click+=(_,_)=>host?.Coordinator.SetController(null); remove.Click+=(_,_)=> { if(Selected() is Guid id) host?.Coordinator.Remove(id); };
            panel.Controls.Add(Theme.Row(grant,revoke,remove)); RefreshViewers();
            var chat=Theme.Button("Chat");chat.Click+=(_,_)=>{if(Selected()is Guid id)OpenChat(id);};
            var sendFile=Theme.Button("Send file");sendFile.Click+=async(_,_)=>{if(Selected()is not Guid id||host is null)return;try{if(!host.Coordinator.Status(id).Files)throw new IOException("File transfers were not allowed when this viewer connected.");string? file=SessionFiles.PickSend(this);if(file is not null)await Files(id).SendAsync(file,hosting!.Token);}catch(Exception ex){ShowError(ex.Message);}};
            var clipboard=Theme.Button("Send clipboard");clipboard.Click+=async(_,_)=>{if(Selected()is not Guid id||host is null)return;try{if(Clipboard.ContainsText()){string text=Clipboard.GetText();if(text.Length>2000)throw new IOException("Clipboard text is limited to 2,000 characters.");await host.SendMessageAsync(id,Wire.Json(MessageKind.Clipboard,text),hosting!.Token);}}catch(Exception ex){ShowError(ex.Message);}};
            panel.Controls.Add(Theme.Row(chat,sendFile,clipboard));
        }
        else {
            bool favorites=page=="favorites", recent=page=="recent";
            var panel=Page(favorites?"Favorites":recent?"Recent sessions":"Address book","Select a device to connect. Addresses belong to your configured OpenDesk network.");
            var add=Theme.Button("Add device",true); add.Click+=(_,_)=>EditContact(); panel.Controls.Add(Theme.Row(add));
            var list=new ListView { View=View.Details,FullRowSelect=true,MultiSelect=false,Dock=DockStyle.Fill,Height=400 };
            list.Columns.Add("Device",260); list.Columns.Add("Address",180); list.Columns.Add("Favorite",90); list.Columns.Add("Last connected",180);
            foreach(var c in settings.Contacts.Where(c=>!favorites||c.Favorite).OrderByDescending(c=>c.LastConnected)) {
                var row=new ListViewItem(c.Name){Tag=c}; row.SubItems.AddRange([DeviceProof.FormatId(c.Id),c.Favorite?"★":"",c.LastConnected==default?"Never":c.LastConnected.ToLocalTime().ToString("g")]); list.Items.Add(row);
            }
            list.DoubleClick+=async(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) { remote.Text=c.Id; await ConnectAsync(); } };
            panel.Controls.Add(list); var favorite=Theme.Button("Toggle favorite"); var edit=Theme.Button("Rename"); var remove=Theme.Button("Remove");
            favorite.Click+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) { settings.Contacts.Remove(c); settings.Contacts.Insert(0,c with {Favorite=!c.Favorite}); settings.Save(); ShowPage(page); } };
            edit.Click+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) EditContact(c); };
            remove.Click+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) { settings.Contacts.Remove(c);settings.Save();ShowPage(page); } };
            panel.Controls.Add(Theme.Row(favorite,edit,remove));
        }
    }
    private Control ContactTiles(IEnumerable<Contact> contacts)
    {
        var tiles=Theme.Row(); int count=0;
        foreach(var c in contacts) {
            var tile=Theme.Button($"▣  {c.Name}\n{DeviceProof.FormatId(c.Id)}"); tile.AutoSize=false; tile.Size=new(235,86); tile.TextAlign=ContentAlignment.MiddleLeft;
            tile.Click+=async(_,_)=> { remote.Text=c.Id; await ConnectAsync(); }; tiles.Controls.Add(tile); count++;
        }
        if(count==0) tiles.Controls.Add(Theme.Label("Devices you connect to will appear here.",10)); return tiles;
    }
    private void EditContact(Contact? existing=null)
    {
        using var dialog=new Form { AutoScaleDimensions=new(96,96),AutoScaleMode=AutoScaleMode.Dpi,Font=Font,Text=existing is null?"Add device":"Rename device",ClientSize=new(480,340),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false };
        var panel=Theme.Stack(); var id=new TextBox { Width=330,Text=existing?.Id??"",PlaceholderText="Nine-digit device address" }; var name=new TextBox { Width=330,Text=existing?.Name??"",MaxLength=64,PlaceholderText="Friendly name" };
        panel.Controls.Add(Theme.Label("Device address"));panel.Controls.Add(id);panel.Controls.Add(Theme.Label("Name"));panel.Controls.Add(name);var save=Theme.Button("Save",true);panel.Controls.Add(save);dialog.Controls.Add(panel);
        save.Click+=(_,_)=> { try { string number=DeviceProof.NormalizeId(id.Text);if(string.IsNullOrWhiteSpace(name.Text))throw new FormatException("Enter a device name.");settings.Contacts.RemoveAll(c=>c.Id==number||c==existing);settings.Contacts.Insert(0,new(number,name.Text.Trim(),existing?.Favorite??false,existing?.LastConnected??default));settings.Save();dialog.DialogResult=DialogResult.OK; }catch(Exception ex){MessageBox.Show(dialog,ex.Message);} };
        if(dialog.ShowDialog(this)==DialogResult.OK)ShowPage("contacts");
    }
    private (int Width,long Quality) CaptureProfile()=>quality.SelectedIndex switch{0=>(1024,50),2=>(1600,80),_=>(1280,65)};
    private void UpdateCapture(){if(host is not null&&desktop is not null){var profile=CaptureProfile();host.Coordinator.ChangeDisplay(()=>desktop.Select(screens[monitor.SelectedIndex].Bounds,profile.Width,profile.Quality));}}
    private Guid? Selected()=>viewers.SelectedItems.Count==1 && viewers.SelectedItems[0].Tag is Guid id?id:null;
    private void Ui(Action action){if(!IsDisposed&&IsHandleCreated){try{BeginInvoke(action);}catch(InvalidOperationException){}}}
    private void RefreshViewers()
    {
        Guid? selected=Selected();viewers.BeginUpdate();viewers.Items.Clear();
        foreach(var v in host?.Coordinator.Viewers??[]){var row=new ListViewItem(v.Name){Tag=v.Id,Selected=v.Id==selected};row.SubItems.Add(v.Control?"Keyboard + mouse":"View only");viewers.Items.Add(row);}viewers.EndUpdate();
        if(host is not null)bottom.Text=host.IsRunning?$"Online · {viewers.Items.Count}/{host.Coordinator.Capacity} viewers · TLS encrypted":"Sharing stopped · go offline, then reconnect";
    }
    private async Task GoOnlineAsync()
    {
        if(host is not null||connectingHost)return;
        if(settings.ServiceUrl.Length==0){Configure();return;}
        connectingHost=true;
        online.Enabled=false;network.Text="Connecting to device network…";
        try{
            hosting=new();var token=hosting.Token;directory=new(settings.ServiceUrl,settings.ServicePin);var info=await directory.InfoAsync(token);token.ThrowIfCancellationRequested();
            if(info.Protocol!=1 || info.RelayPort is <1 or >65535)throw new InvalidDataException("Unsupported device service.");
            var loadedKey=await Task.Run(deviceKeyFactory);if(token.IsCancellationRequested){loadedKey.Dispose();return;}deviceKey=loadedKey;var screen=screens[monitor.SelectedIndex];var profile=CaptureProfile();desktop=new(screen.Bounds,profile.Width,profile.Quality);
            var source=desktop;int capacity=(int)limit.Value;
            var createdHost=await Task.Run(()=>new HostService(source.Capture,inputOverride??new WindowsInput(()=>source.Bounds),RequestApprovalAsync,capacity));
            if(token.IsCancellationRequested){await createdHost.DisposeAsync();return;}host=createdHost;
            host.MessageReceived=ReceiveMessageAsync;
            host.PeerDisconnected+=id=>{if(transfers.TryRemove(id,out var file))fileCleanup.Add(file.DisposeAsync().AsTask());Ui(()=>{if(chats.TryGetValue(id,out var chat))chat.Append("System","Viewer disconnected");});};
            host.Coordinator.Changed+=()=>Ui(RefreshViewers);host.Notice+=text=>Ui(()=>bottom.Text=text);
            await host.StartRelayAsync(directory.Endpoint.Host,info.RelayPort,true);
            var identity=await directory.RegisterAsync(deviceKey,host.Invitation,host.RelayOwner,hosting.Token);
            settings.DeviceId=identity.Id;settings.Save();deviceId.Text=DeviceProof.FormatId(identity.Id);network.Text="● Online · ready for incoming connections";network.ForeColor=Color.FromArgb(34,139,84);
            limit.Enabled=false;stopAll.Enabled=true;online.Text="Online";online.BackColor=Color.FromArgb(232,234,237);online.Cursor=Cursors.Default;registration=MaintainRegistrationAsync(hosting.Token);RefreshViewers();
        }catch(Exception ex){await StopAsync();network.Text="Offline · service unavailable";ShowError(ex.Message);}finally{connectingHost=false;}
    }
    private async Task MaintainRegistrationAsync(CancellationToken ct)
    {
        try{using var timer=new PeriodicTimer(TimeSpan.FromSeconds(20));while(await timer.WaitForNextTickAsync(ct)){
            if(host is null||!host.IsRunning)throw new IOException("Sharing connection ended.");
            await directory!.RegisterAsync(deviceKey!,host.Invitation,host.RelayOwner,ct);
        }}catch(Exception ex)when(ex is IOException or HttpRequestException or OperationCanceledException){if(!ct.IsCancellationRequested)Ui(async()=>{await StopAsync();network.Text="Offline · network connection ended";});}
    }
    private async Task StopAsync()
    {
        hosting?.Cancel();if(registration is not null){try{await registration;}catch(OperationCanceledException){}}registration=null;
        if(host is not null){await host.DisposeAsync();host=null;}desktop?.Dispose();desktop=null;foreach(var transfer in transfers.Values)await transfer.DisposeAsync();transfers.Clear();await Task.WhenAll(fileCleanup);fileCleanup.Clear();foreach(var chat in chats.Values)chat.Close();chats.Clear();directory?.Dispose();directory=null;deviceKey?.Dispose();deviceKey=null;hosting?.Dispose();hosting=null;
        foreach(var request in requests.ToArray())request.Close();viewers.Items.Clear();online.Text="Go online";online.BackColor=Theme.Green;online.Cursor=Cursors.Hand;online.Enabled=true;stopAll.Enabled=false;monitor.Enabled=limit.Enabled=true;network.Text="● Offline · incoming connections are stopped";network.ForeColor=Theme.Ink;bottom.Text="Offline";
    }
    private void Configure()
    {
        if(host is not null||connectingHost){ShowError("Stop incoming sessions before changing the device service.");return;}
        using var dialog=new ServiceSettingsForm(settings);if(dialog.ShowDialog(this)==DialogResult.OK){deviceId.Text=settings.DeviceId.Length==9?DeviceProof.FormatId(settings.DeviceId):"Not registered";_ = GoOnlineAsync();}
    }
    private async Task ConnectAsync()
    {
        if(joining is not null)return;
        if(settings.ServiceUrl.Length==0){Configure();return;}
        connect.Enabled=false;joining=new();
        try{
            string id=DeviceProof.NormalizeId(remote.Text);using var lookup=new DirectoryClient(settings.ServiceUrl,settings.ServicePin);bottom.Text="Finding " + DeviceProof.FormatId(id) + "…";
            var route=await lookup.LookupAsync(id,joining.Token);bottom.Text="Waiting for the remote host to approve…";
            var session=await ViewerSession.ConnectAsync(route.Session,settings.DisplayName,joining.Token);
            settings.Remember(id,DeviceProof.FormatId(id));if(currentPage=="home")ShowPage("home");var window=new ViewerForm(session,DeviceProof.FormatId(id)){Opacity=Opacity,ShowInTaskbar=ShowInTaskbar};windows.Add(window);window.FormClosed+=(_,_)=>{windows.Remove(window);window.Dispose();};window.Show();bottom.Text="Connected to " + DeviceProof.FormatId(id);
        }catch(OperationCanceledException){bottom.Text="Connection cancelled or timed out";}catch(Exception ex){ShowError(ex.Message);bottom.Text="Connection ended";}
        finally{joining.Dispose();joining=null;connect.Enabled=true;}
    }
    private async Task<Approval> RequestApprovalAsync(string name,CancellationToken ct)
    {
        var answer=new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(()=>{if(ct.IsCancellationRequested||closing){answer.TrySetResult(new(false));return;}var dialog=new ApprovalForm(name){Opacity=Opacity,ShowInTaskbar=false};requests.Add(dialog);dialog.FormClosed+=(_,_)=>{requests.Remove(dialog);dialog.Dispose();};_ = CompleteAsync(dialog);dialog.Show(this);dialog.Activate();});
        return await answer.Task.WaitAsync(ct);
        async Task CompleteAsync(ApprovalForm dialog){using var cancellation=ct.Register(()=>Ui(()=>{if(!dialog.IsDisposed)dialog.Close();}));answer.TrySetResult(await dialog.Answer);}
    }
    private void Preview()
    {
        try{var profile=CaptureProfile();using var capture=new ScreenCapture(screens[monitor.SelectedIndex].Bounds,profile.Width,profile.Quality);var f=capture.Capture();using var buffer=new MemoryStream(f.Jpeg);using var decoded=Image.FromStream(buffer);var image=new Bitmap(decoded);var window=new Form{Text="OpenDesk · Display preview",ClientSize=new(800,500)};window.Controls.Add(new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=image});window.FormClosed+=(_,_)=>{image.Dispose();window.Dispose();};window.Show(this);}catch(Exception ex){ShowError(ex.Message);}
    }
    private FileTransfer Files(Guid id)=>transfers.GetOrAdd(id,_=>{var files=new FileTransfer((message,ct)=>host!.SendMessageAsync(id,message,ct),(offer,ct)=>SessionFiles.ChooseSaveAsync(this,offer,ct));files.Notice+=text=>Ui(()=>bottom.Text=text);return files;});
    private void OpenChat(Guid id)
    {
        if(host is null)return;
        if(!chats.TryGetValue(id,out var chat)||chat.IsDisposed){chat=new ChatForm(host.Coordinator.Viewers.FirstOrDefault(v=>v.Id==id)?.Name??"Viewer",text=>host.SendMessageAsync(id,Wire.Json(MessageKind.Chat,text),hosting!.Token));chats[id]=chat;}
        if(!chat.Visible)chat.Show(this);else chat.Activate();
    }
    private async Task ReceiveMessageAsync(Guid id,OpenDesk.Core.Message message,CancellationToken ct)
    {
        if(message.Kind is MessageKind.Chat or MessageKind.Clipboard){string text=Wire.Decode<string>(message);if(text.Length>2000)throw new InvalidDataException("Text message is too long.");
            if(message.Kind==MessageKind.Chat)Ui(()=>{OpenChat(id);if(chats.TryGetValue(id,out var chat))chat.Append("Remote",text);});
            else Ui(()=>{try{Clipboard.SetText(text);bottom.Text="Received clipboard text";}catch(System.Runtime.InteropServices.ExternalException){bottom.Text="Clipboard is busy";}});
        }else await Files(id).ReceiveAsync(message,ct);
    }
    private void ShowError(string message){if(!closing&&!IsDisposed)MessageBox.Show(this,message,"OpenDesk",MessageBoxButtons.OK,MessageBoxIcon.Information);}
}
