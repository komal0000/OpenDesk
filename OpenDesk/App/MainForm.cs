using System.Security.Cryptography;
using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class MainForm : Form
{
    private readonly ClientSettings settings;
    private readonly Func<ECDsa> deviceKeyFactory;
    private readonly IInputSink? inputOverride;
    private readonly TextBox remote = new() { Width=290, Font=new Font("Segoe UI",15), PlaceholderText="Enter remote address", MaxLength=32 };
    private readonly Button connect = Theme.Button("Connect",true);
    private readonly Label deviceId = Theme.Label("Not registered",30,true), network = Theme.Label("Offline · incoming connections are stopped",10);
    private readonly Button online = Theme.Button("Go online",true), stopAll = Theme.Button("Stop All");
    private readonly ComboBox monitor = new() { Width=260, DropDownStyle=ComboBoxStyle.DropDownList };
    private readonly ComboBox quality = new() { Width=220,DropDownStyle=ComboBoxStyle.DropDownList };
    private readonly NumericUpDown limit = new() { Minimum=1,Maximum=10,Value=5,Width=60 };
    private readonly Panel content = new() { Dock=DockStyle.Fill,Padding=new Padding(32,24,32,16),BackColor=Theme.Paper };
    private readonly Label bottom = Theme.Label("Ready",9);
    private readonly ListView viewers = new() { View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,Dock=DockStyle.Fill,Height=180 };
    private readonly List<ViewerForm> windows=[];
    private readonly List<ApprovalForm> requests=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,FileTransfer> transfers=[];
    private readonly Dictionary<Guid,ChatForm> chats=[];
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> fileCleanup=[];
    private readonly Screen[] screens=Screen.AllScreens;
    private HostEngine? host;
    private ECDsa? deviceKey;
    private CancellationTokenSource? hosting;
    private bool closing;
    private bool connectingHost;
    private readonly bool autoConnect;
    private string currentPage="home";
    private bool sharingExpanded;
    internal bool IsOnline=>host?.IsConnected==true&&settings.DeviceId.Length==9;
    internal string DeviceAddress=>settings.DeviceId;
    internal Task GoOfflineAsync()=>StopAsync();

    public MainForm(bool autoConnect=true,ClientSettings? profile=null,Func<ECDsa>? keyFactory=null,IInputSink? inputOverride=null)
    {
        SuspendLayout();
        settings=profile??ClientSettings.Load();deviceKeyFactory=keyFactory??(()=>ClientSettings.LoadDeviceKey());this.inputOverride=inputOverride;
        this.autoConnect=autoConnect;
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;DoubleBuffered=true;
        Text="OpenDesk · New Session"; ClientSize=new(1160,790); MinimumSize=new(800,560); StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10); BackColor=Theme.Paper;
        var top=new TableLayoutPanel { Dock=DockStyle.Top,Height=72,BackColor=Color.White,Padding=new Padding(16,16,24,16),ColumnCount=4,RowCount=1,TabIndex=0 };
        top.ColumnStyles.Add(new(SizeType.Absolute,184));top.ColumnStyles.Add(new(SizeType.Absolute,120));top.ColumnStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.Absolute,112));top.RowStyles.Add(new(SizeType.Percent,100));
        var brand=Theme.Label("OpenDesk",18,true);brand.Name="Brand";brand.AutoSize=false;brand.ForeColor=Theme.Accent;brand.Dock=DockStyle.Fill;brand.TextAlign=ContentAlignment.MiddleLeft;brand.Padding=new(36,0,0,0);brand.Margin=Padding.Empty;
        brand.Paint+=(_,e)=>{int u=(int)Math.Round(24*brand.DeviceDpi/96f);Theme.DrawIcon(e.Graphics,"brand",new(0,(brand.Height-u)/2,u,u),Theme.Accent);};
        var addressLabel=Theme.Label("Remote address",9,true);addressLabel.AutoSize=false;addressLabel.Dock=DockStyle.Fill;addressLabel.TextAlign=ContentAlignment.MiddleLeft;addressLabel.Margin=new(16,0,0,0);
        remote.Dock=DockStyle.Fill;remote.Margin=new(8,3,12,0);remote.Font=new Font("Segoe UI",13);remote.PlaceholderText="Enter remote address";remote.AccessibleName="Remote address";remote.TabIndex=0;
        connect.Dock=DockStyle.Fill;connect.AutoSize=false;connect.MinimumSize=Size.Empty;connect.Margin=Padding.Empty;connect.TabIndex=1;
        top.Controls.Add(brand,0,0);top.Controls.Add(addressLabel,1,0);top.Controls.Add(remote,2,0);top.Controls.Add(connect,3,0);
        var side=new FlowLayoutPanel { Name="Navigation",Dock=DockStyle.Left,Width=200,BackColor=Theme.Sidebar,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(8,24,8,8),TabIndex=1 };
        foreach(var item in new[] { ("New session", "home"),("Recent sessions","recent"),("Favorites","favorites"),("Address book","contacts"),("Incoming sessions","incoming"),("Settings","settings") }) {
            var nav=new Theme.NavigationButton { Text=item.Item1,AccessibleName=item.Item1,Icon=item.Item2,Name="Nav-"+item.Item2,AutoSize=false,Size=new(184,44),Margin=new(0,0,0,4),Padding=new(40,0,0,0),TextAlign=ContentAlignment.MiddleLeft,FlatStyle=FlatStyle.Flat,BackColor=side.BackColor,ForeColor=Theme.Muted,Cursor=Cursors.Hand,UseVisualStyleBackColor=false };
            nav.FlatAppearance.BorderSize=0;nav.FlatAppearance.MouseOverBackColor=Color.White;nav.FlatAppearance.MouseDownBackColor=Theme.Selection;
            string page=item.Item2; nav.Click+=(_,_)=> { if(page=="settings") Configure(); else ShowPage(page); }; side.Controls.Add(nav);
        }
        var footer=new Panel { Dock=DockStyle.Bottom,Height=32,BackColor=Color.White,Padding=new Padding(16,0,16,0),TabStop=false };bottom.AutoSize=false;bottom.Dock=DockStyle.Fill;bottom.Margin=Padding.Empty;bottom.TextAlign=ContentAlignment.MiddleLeft;bottom.ForeColor=Theme.Muted;footer.Controls.Add(bottom);
        content.TabIndex=2;
        Controls.Add(content); Controls.Add(side); Controls.Add(footer); Controls.Add(top);
        for(int i=0;i<screens.Length;i++) monitor.Items.Add($"Display {i+1} · {screens[i].Bounds.Width} × {screens[i].Bounds.Height}");
        monitor.SelectedIndex=Math.Max(0,Array.FindIndex(screens,s=>s.Primary));
        quality.Items.AddRange([..new[]{"Fast","Balanced","Sharp"}.Zip(VideoPipeline.Profiles,(name,p)=>$"{name} · {p.Width} px")]);quality.SelectedIndex=Math.Clamp(settings.VideoQuality,0,VideoPipeline.Profiles.Length-1);
        monitor.SelectedIndexChanged+=(_,_)=>UpdateCapture();quality.SelectedIndexChanged+=(_,_)=>{settings.VideoQuality=quality.SelectedIndex;settings.Save();UpdateCapture();};
        viewers.Columns.Add("Connected viewer",340); viewers.Columns.Add("Permission",180);
        Theme.List(viewers);viewers.AccessibleName="Incoming viewers";Theme.Destructive(stopAll);
        monitor.AccessibleName="Shared display";quality.AccessibleName="Screen quality";limit.AccessibleName="Viewer limit";
        connect.Click+=(_,_)=>Connect(); remote.KeyDown+=(_,e)=> { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; Connect(); } };
        remote.Name="RemoteAddress";connect.Name="Connect";
        online.Click+=async(_,_)=>await GoOnlineAsync(); stopAll.Click+=async(_,_)=>await StopAsync(); stopAll.Enabled=false;
        deviceId.Text=settings.DeviceId.Length==9 ? DeviceProof.FormatId(settings.DeviceId) : "Not registered";
        deviceId.Margin=new(0,0,16,0);network.Margin=new(0,4,0,4);
        ShowPage("home");
        Shown+=async(_,_)=> { if(this.autoConnect && settings.ServiceUrl.Length>0) await GoOnlineAsync(); };
        FormClosing+=async(_,e)=> {
            if(closing) return; e.Cancel=true; closing=true; Enabled=false;
            foreach(var window in windows.ToArray()) window.Close(); await StopAsync(); Close();
        };
        ResumeLayout(true);
    }
    private Panel Page(string title,string subtitle)
    {
        foreach(var shared in new Control[]{deviceId,network,online,stopAll,monitor,quality,limit,viewers})shared.Parent?.Controls.Remove(shared);
        foreach(Control old in content.Controls.Cast<Control>().ToArray()){content.Controls.Remove(old);old.Dispose();}Text="OpenDesk · " + title;
        var panel=Theme.Stack(); panel.Padding=Padding.Empty; panel.BackColor=Theme.Paper;
        panel.Controls.Add(Theme.Label(title,20,true));var desc=Theme.Description(subtitle);panel.Controls.Add(desc);
        content.Controls.Add(panel); return panel;
    }
    private void ShowPage(string page)
    {
        currentPage=page;
        foreach(var nav in Controls.Find("Navigation",true).Single().Controls.OfType<Theme.NavigationButton>()) {
            nav.Selected=nav.Name=="Nav-"+page;nav.BackColor=nav.Selected?Theme.Selection:Theme.Sidebar;nav.ForeColor=nav.Selected?Theme.Accent:Theme.Muted;
            nav.AccessibleDescription=nav.Selected?"Current page":null;nav.Invalidate();
        }
        try {
        if(page=="home") {
            var panel=Page("New Session","Share your address, or enter a remote address above to connect.");
            var card=Theme.Card();card.Name="AddressCard";
            var title=Theme.Label("Your address",11,true);title.Margin=new(0,0,0,4);deviceId.ForeColor=Theme.Accent;deviceId.Name="DeviceAddress";network.ForeColor=IsOnline?Theme.Success:Theme.Muted;
            var copy=Theme.Button("Copy address");copy.Name="CopyAddress";copy.Margin=new(0,8,0,0);copy.Enabled=settings.DeviceId.Length==9;
            copy.Click+=(_,_)=> { if(settings.DeviceId.Length==9) { try { Clipboard.SetText(settings.DeviceId);bottom.Text="Address copied"; } catch(System.Runtime.InteropServices.ExternalException) { bottom.Text="Clipboard is busy. Try again."; } } };
            var addressRow=Theme.Row(deviceId,copy);addressRow.Name="LocalAddressRow";card.Controls.AddRange([title,addressRow,network]);panel.Controls.Add(card);
            panel.Controls.Add(Theme.Row(online,stopAll));
            panel.Controls.Add(Theme.Label("Recent sessions",12,true));panel.Controls.Add(ContactTiles(settings.Contacts.Where(c=>c.LastConnected!=default).OrderByDescending(c=>c.LastConnected).Take(6)));
            var sharing=Theme.Stack();sharing.Dock=DockStyle.None;sharing.Name="SharingOptions";sharing.AutoScroll=false;sharing.AutoSize=true;sharing.AutoSizeMode=AutoSizeMode.GrowAndShrink;sharing.Padding=new(16);sharing.BackColor=Color.White;sharing.Visible=sharingExpanded;
            var preview=Theme.Button("Preview");preview.Click+=(_,_)=>Preview();
            sharing.Controls.Add(Theme.Label("Shared display",10,true));sharing.Controls.Add(Theme.Row(monitor,preview));
            sharing.Controls.Add(Theme.Label("Screen quality",10,true));sharing.Controls.Add(Theme.Row(quality,Theme.Label("Viewer limit"),limit));
            var toggle=Theme.Button(sharingExpanded?"Hide display & sharing":"Display & sharing");toggle.Name="ToggleSharing";toggle.AccessibleDescription=sharingExpanded?"Expanded":"Collapsed";
            toggle.Click+=(_,_)=>{sharingExpanded=!sharingExpanded;sharing.Visible=sharingExpanded;toggle.Text=sharingExpanded?"Hide display & sharing":"Display & sharing";toggle.AccessibleDescription=sharingExpanded?"Expanded":"Collapsed";panel.PerformLayout();};
            panel.Controls.Add(Theme.Row(toggle));panel.Controls.Add(sharing);
            panel.Controls.Add(Theme.Description("Every connection needs your approval. Only one viewer can control this computer at a time."));
        }
        else if(page=="incoming") {
            var panel=Page("Incoming sessions","Approve requests, manage connected viewers, and choose who has control.");
            var noViewers=Theme.Description("No incoming sessions. Approved viewers will appear here.");noViewers.Name="NoViewers";panel.Controls.Add(noViewers);
            panel.Controls.Add(viewers); panel.Controls.Add(Theme.Row(online,stopAll));
            var grant=Theme.Button("Give control",true); var revoke=Theme.Button("Revoke control"); var remove=Theme.Button("Disconnect viewer");
            Theme.Destructive(remove);
            grant.Tag=revoke.Tag=remove.Tag="LocalPermission";
            grant.Click+=(_,_)=> { if(Selected() is Guid id) { try { host?.Coordinator.SetController(id); } catch(InvalidOperationException) { RefreshViewers(); } } };
            revoke.Click+=(_,_)=>host?.Coordinator.SetController(null); remove.Click+=(_,_)=> { if(Selected() is Guid id) host?.Coordinator.Remove(id); };
            panel.Controls.Add(Theme.Row(grant,revoke,remove)); RefreshViewers();
            var chat=Theme.Button("Chat");chat.Click+=(_,_)=>{if(Selected()is Guid id)OpenChat(id);};
            var sendFile=Theme.Button("Send file");sendFile.Click+=async(_,_)=>{if(Selected()is not Guid id||host is null)return;try{if(!host.Coordinator.Status(id).Files)throw new IOException("File transfers were not allowed when this viewer connected.");string? file=SessionFiles.PickSend(this);if(file is not null)await Files(id).SendAsync(file,hosting!.Token);}catch(Exception ex){ShowError(ex.Message);}};
            var clipboard=Theme.Button("Send clipboard");clipboard.Click+=async(_,_)=>{if(Selected()is not Guid id||host is null)return;try{if(Clipboard.ContainsText()){string text=Clipboard.GetText();if(text.Length>2000)throw new IOException("Clipboard text is limited to 2,000 characters.");await host.Peers.SendMessageAsync(id,Wire.Json(MessageKind.Clipboard,text),hosting!.Token);}}catch(Exception ex){ShowError(ex.Message);}};
            panel.Controls.Add(Theme.Row(chat,sendFile,clipboard));
        }
        else {
            bool favorites=page=="favorites", recent=page=="recent";
            var panel=Page(favorites?"Favorites":recent?"Recent sessions":"Address book","Select a device to connect. Addresses belong to your configured OpenDesk network.");
            if(favorites||recent){
                var devices=settings.Contacts.Where(c=>favorites?c.Favorite:c.LastConnected!=default).OrderByDescending(c=>c.LastConnected);
                panel.Controls.Add(ContactTiles(devices,favorites));var manage=Theme.Button("Manage address book");manage.Click+=(_,_)=>ShowPage("contacts");panel.Controls.Add(Theme.Row(manage));return;
            }
            var add=Theme.Button("Add device",true); add.Click+=(_,_)=>EditContact(); panel.Controls.Add(Theme.Row(add));
            var list=new ListView { View=View.Details,FullRowSelect=true,MultiSelect=false,Dock=DockStyle.Fill,Height=400 };
            list.Columns.Add("Device",260); list.Columns.Add("Address",180); list.Columns.Add("Favorite",90); list.Columns.Add("Last connected",180);
            Theme.List(list);list.AccessibleName="Address book devices";
            foreach(var c in settings.Contacts.Where(c=>!favorites||c.Favorite).OrderByDescending(c=>c.LastConnected)) {
                var row=new ListViewItem(c.Name){Tag=c}; row.SubItems.AddRange([DeviceProof.FormatId(c.Id),c.Favorite?"Yes":"No",c.LastConnected==default?"Never":c.LastConnected.ToLocalTime().ToString("g")]); list.Items.Add(row);
            }
            list.DoubleClick+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) { remote.Text=c.Id; Connect(); } };
            list.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter&&list.SelectedItems.Count==1&&list.SelectedItems[0].Tag is Contact c){e.SuppressKeyPress=true;remote.Text=c.Id;Connect();}};
            if(list.Items.Count==0)panel.Controls.Add(Theme.Description("Your address book is empty. Add a device to keep it close at hand."));
            panel.Controls.Add(list); var favorite=Theme.Button("Toggle favorite"); var edit=Theme.Button("Rename"); var remove=Theme.Button("Remove");
            Theme.Destructive(remove);
            favorite.Click+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) { settings.Contacts.Remove(c); settings.Contacts.Insert(0,c with {Favorite=!c.Favorite}); settings.Save(); ShowPage(page); } };
            edit.Click+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) EditContact(c); };
            remove.Click+=(_,_)=> { if(list.SelectedItems.Count==1 && list.SelectedItems[0].Tag is Contact c) { settings.Contacts.Remove(c);settings.KnownKeys.Remove(c.Id);settings.Save();ShowPage(page); } };
            panel.Controls.Add(Theme.Row(favorite,edit,remove));
        }
        } finally {
            if(IsHandleCreated&&content.Controls.Count>0) {
                var panel=content.Controls[0];
                var retained=new Control[]{deviceId,network,online,stopAll,monitor,quality,limit,viewers}
                    .Where(c=>c.Parent is not null).Select(c=>(Control:c,Parent:c.Parent!,Index:c.Parent!.Controls.GetChildIndex(c))).ToArray();
                panel.SuspendLayout();foreach(var parent in retained.Select(c=>c.Parent).Distinct())parent.SuspendLayout();
                // Only the new controls use 96-DPI dimensions; retained session controls are already scaled.
                foreach(var item in retained)item.Parent.Controls.Remove(item.Control);
                panel.Scale(new SizeF(DeviceDpi/96f,DeviceDpi/96f));
                foreach(var item in retained){item.Parent.Controls.Add(item.Control);item.Parent.Controls.SetChildIndex(item.Control,item.Index);}
                foreach(var parent in retained.Select(c=>c.Parent).Distinct())parent.ResumeLayout(true);panel.ResumeLayout(true);
            }
        }
    }
    private Control ContactTiles(IEnumerable<Contact> contacts,bool favorites=false)
    {
        var tiles=Theme.Row();tiles.Name="DeviceCards";int count=0;
        foreach(var c in contacts) {
            var tile=new Theme.DeviceCard(c);
            tile.Click+=(_,_)=> { remote.Text=c.Id; Connect(); }; tiles.Controls.Add(tile); count++;
        }
        if(count==0) {
            var empty=Theme.Card();empty.Name="EmptyDevices";empty.Width=720;empty.Padding=new(24);
            empty.Controls.Add(Theme.Label(favorites?"No favorites yet":"No recent sessions",11,true));empty.Controls.Add(Theme.Description(favorites?"Mark a device as a favorite in your address book.":"Devices appear here after you connect using a remote address."));tiles.Controls.Add(empty);
        }
        tiles.SizeChanged+=(_,_)=>{foreach(Control tile in tiles.Controls)tile.Width=Math.Min(tile is Theme.DeviceCard?(int)Math.Round(244*tiles.DeviceDpi/96f):tiles.ClientSize.Width,Math.Max(1,tiles.ClientSize.Width-tile.Margin.Horizontal));};return tiles;
    }
    private void EditContact(Contact? existing=null)
    {
        using var dialog=ContactDialog(existing);
        if(dialog.ShowDialog(this)==DialogResult.OK)ShowPage("contacts");
    }
    internal Form ContactDialog(Contact? existing=null)
    {
        var dialog=new Form();dialog.SuspendLayout();
        dialog.AutoScaleDimensions=new(96,96);dialog.AutoScaleMode=AutoScaleMode.Dpi;dialog.Font=Font;dialog.Text=existing is null?"OpenDesk · Add device":"OpenDesk · Rename device";dialog.ClientSize=new(480,360);dialog.StartPosition=FormStartPosition.CenterParent;dialog.FormBorderStyle=FormBorderStyle.FixedDialog;dialog.MaximizeBox=false;dialog.MinimizeBox=false;dialog.BackColor=Color.White;
        var panel=Theme.Stack(); var id=new TextBox { Width=330,Text=existing?.Id??"",PlaceholderText="Nine-digit device address" }; var name=new TextBox { Width=330,Text=existing?.Name??"",MaxLength=64,PlaceholderText="Friendly name" };
        panel.Controls.Add(Theme.Label(existing is null?"Add a device":"Device details",18,true));panel.Controls.Add(Theme.Description("Save a name and address for your next connection."));
        id.AccessibleName="Device address";name.AccessibleName="Device name";id.TabIndex=0;name.TabIndex=1;
        panel.Controls.Add(Theme.Label("Device address"));panel.Controls.Add(id);panel.Controls.Add(Theme.Label("Name"));panel.Controls.Add(name);var save=Theme.Button("Save",true);var cancel=Theme.Button("Cancel");cancel.DialogResult=DialogResult.Cancel;panel.Controls.Add(Theme.Row(save,cancel));dialog.Controls.Add(panel);dialog.AcceptButton=save;dialog.CancelButton=cancel;
        save.Click+=(_,_)=> { try { string number=DeviceProof.NormalizeId(id.Text);if(string.IsNullOrWhiteSpace(name.Text))throw new FormatException("Enter a device name.");settings.Contacts.RemoveAll(c=>c.Id==number||c==existing);settings.Contacts.Insert(0,new(number,name.Text.Trim(),existing?.Favorite??false,existing?.LastConnected??default));settings.Save();dialog.DialogResult=DialogResult.OK; }catch(Exception ex){MessageBox.Show(dialog,ex.Message);} };
        dialog.ResumeLayout(true);return dialog;
    }
    private VideoPipeline.Profile CaptureProfile()=>VideoPipeline.Profiles[quality.SelectedIndex];
    private void UpdateCapture()=>host?.ChangeDisplay(screens[monitor.SelectedIndex].Bounds,CaptureProfile());
    private Guid? Selected()=>viewers.SelectedItems.Count==1 && viewers.SelectedItems[0].Tag is Guid id?id:null;
    private void Ui(Action action){if(!IsDisposed&&IsHandleCreated){try{BeginInvoke(action);}catch(InvalidOperationException){}}}
    private void RefreshViewers()
    {
        Guid? selected=Selected();viewers.BeginUpdate();viewers.Items.Clear();
        foreach(var v in host?.Coordinator.Viewers??[]){var row=new ListViewItem(v.Name){Tag=v.Id,Selected=v.Id==selected};row.SubItems.Add((v.Control?"Keyboard + mouse":"View only")+(host!.Route(v.Id) is {} route?" · "+route:""));viewers.Items.Add(row);}viewers.EndUpdate();
        foreach(var empty in Controls.Find("NoViewers",true))empty.Visible=viewers.Items.Count==0;
        if(host is not null)bottom.Text=host.IsConnected?$"Online · {viewers.Items.Count}/{host.Coordinator.Capacity} viewers · end-to-end encrypted":"Reconnecting to the device network…";
    }
    private async Task GoOnlineAsync()
    {
        if(host is not null||connectingHost)return;
        if(settings.ServiceUrl.Length==0){Configure();return;}
        connectingHost=true;
        online.Enabled=false;network.Text="Connecting to device network…";
        try{
            hosting=new();var token=hosting.Token;
            using(var directory=new DirectoryClient(settings.ServiceUrl,settings.ServicePin)){
                if(!await directory.SupportsSignalingAsync(token))throw new InvalidDataException("This device service is too old for OpenDesk 0.4. Update the service (see deploy/linux/README.md).");
            }
            var loadedKey=await Task.Run(deviceKeyFactory);if(token.IsCancellationRequested){loadedKey.Dispose();return;}deviceKey=loadedKey;
            host=new HostEngine(this,settings,deviceKey,inputOverride,RequestApprovalAsync,(int)limit.Value,screens[monitor.SelectedIndex].Bounds,CaptureProfile());
            host.Peers.MessageReceived=ReceiveMessageAsync;
            host.Peers.PeerDisconnected+=id=>{if(transfers.TryRemove(id,out var file))fileCleanup.Add(file.DisposeAsync().AsTask());Ui(()=>{if(chats.TryGetValue(id,out var chat))chat.Append("System","Viewer disconnected");});};
            host.Coordinator.Changed+=()=>Ui(()=>{RefreshViewers();foreach(var window in windows)window.RefreshInput();});host.Notice+=text=>Ui(()=>bottom.Text=text);
            host.ConnectionChanged+=connected=>Ui(()=>{network.Text=connected?"Online · ready for incoming connections":"Reconnecting to the device network…";network.ForeColor=connected?Theme.Success:Theme.Muted;RefreshViewers();});
            string id=await host.StartAsync(token);
            settings.DeviceId=id;settings.Save();deviceId.Text=DeviceProof.FormatId(id);
            foreach(var copy in Controls.Find("CopyAddress",true))copy.Enabled=true;
            limit.Enabled=false;stopAll.Enabled=true;online.Text="Online";online.BackColor=Theme.Sidebar;online.ForeColor=Theme.Ink;online.Cursor=Cursors.Default;RefreshViewers();
        }catch(Exception ex){await StopAsync();network.Text="Offline · service unavailable";ShowError(ex.Message);}finally{connectingHost=false;}
    }
    private async Task StopAsync()
    {
        hosting?.Cancel();
        if(host is not null){await host.DisposeAsync();host=null;}foreach(var transfer in transfers.Values)await transfer.DisposeAsync();transfers.Clear();await Task.WhenAll(fileCleanup);fileCleanup.Clear();foreach(var chat in chats.Values)chat.Close();chats.Clear();deviceKey?.Dispose();deviceKey=null;hosting?.Dispose();hosting=null;
        foreach(var request in requests.ToArray())request.Close();viewers.Items.Clear();online.Text="Go online";online.BackColor=Theme.Accent;online.ForeColor=Color.White;online.Cursor=Cursors.Hand;online.Enabled=true;stopAll.Enabled=false;monitor.Enabled=limit.Enabled=true;network.Text="Offline · incoming connections are stopped";network.ForeColor=Theme.Muted;bottom.Text="Offline";
    }
    private void Configure()
    {
        if(host is not null||connectingHost){ShowError("Stop incoming sessions before changing the device service.");return;}
        using var dialog=new ServiceSettingsForm(settings);if(dialog.ShowDialog(this)==DialogResult.OK){deviceId.Text=settings.DeviceId.Length==9?DeviceProof.FormatId(settings.DeviceId):"Not registered";foreach(var copy in Controls.Find("CopyAddress",true))copy.Enabled=settings.DeviceId.Length==9;_ = GoOnlineAsync();}
    }
    // The viewer window runs the connection and shows its progress and any failure.
    private void Connect()
    {
        if(settings.ServiceUrl.Length==0){Configure();return;}
        string id;
        try{id=DeviceProof.NormalizeId(remote.Text);}catch(FormatException ex){ShowError(ex.Message);return;}
        if(id==settings.DeviceId){ShowError("That is this computer's own address.");return;}
        var window=new ViewerForm(settings,id,()=>host?.Coordinator.Viewers.Any(v=>v.Control)==true){Opacity=Opacity,ShowInTaskbar=ShowInTaskbar};windows.Add(window);
        window.FormClosed+=(_,_)=>{windows.Remove(window);window.Dispose();if(currentPage=="home"&&!closing)ShowPage("home");};
        window.Show();bottom.Text="Connecting to " + DeviceProof.FormatId(id) + "…";
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
        try{var bounds=screens[monitor.SelectedIndex].Bounds;var image=new Bitmap(bounds.Width,bounds.Height);using(var g=Graphics.FromImage(image))g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);var window=new Form{Text="OpenDesk · Display preview",ClientSize=new(800,500)};window.Controls.Add(new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=image});window.FormClosed+=(_,_)=>{image.Dispose();window.Dispose();};window.Show(this);}catch(Exception ex){ShowError(ex.Message);}
    }
    private FileTransfer Files(Guid id)=>transfers.GetOrAdd(id,_=>{var files=new FileTransfer((message,ct)=>host!.Peers.SendMessageAsync(id,message,ct),(offer,ct)=>SessionFiles.ChooseSaveAsync(this,offer,ct));files.Notice+=text=>Ui(()=>bottom.Text=text);return files;});
    private void OpenChat(Guid id)
    {
        if(host is null)return;
        if(!chats.TryGetValue(id,out var chat)||chat.IsDisposed){chat=new ChatForm(host.Coordinator.Viewers.FirstOrDefault(v=>v.Id==id)?.Name??"Viewer",text=>host.Peers.SendMessageAsync(id,Wire.Json(MessageKind.Chat,text),hosting!.Token));chats[id]=chat;}
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
