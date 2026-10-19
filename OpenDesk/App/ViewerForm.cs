using System.Threading.Channels;
using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class ViewerForm : Form
{
    private readonly ViewerSession session;
    private readonly CancellationTokenSource stop = new();
    private readonly RemoteScreen picture = new() { Dock = DockStyle.Fill, BackColor = Theme.Ink };
    private readonly Label state = Theme.Label("Connected · View only", 10, true);
    private readonly CheckBox enableInput = new() { Text = "Send keyboard and mouse", AutoSize = true, Enabled = false, Margin = new Padding(12,12,12,4) };
    private readonly InputQueue inputs = new();
    private int displayPosted;
    private long lastMove;
    private MouseButtons heldButtons;
    private Task? reading, sending;
    private ChatForm? chat;
    private readonly FileTransfer files;
    private MjpegRecording? recorder;
    private readonly object recordingGate=new();
    private System.Threading.Timer? recordingTimer;
    private long appliedEpoch=-1;
    private readonly Button fileButton=Theme.Button("Send file"),clipboardButton=Theme.Button("Send clipboard"),recordButton=Theme.Button("Record");
    private bool fullScreen;
    private FormWindowState previousState;
    private Rectangle previousBounds;
    internal int RenderedFrames { get; private set; }
    internal bool InputEnabled=>enableInput.Checked&&session.Status.Control;
    internal double LastDecodeMilliseconds {get;private set;}
    internal RemoteScreen ScreenSurface=>picture;

    internal ViewerForm(ViewerSession session, string address)
    {
        this.session = session;AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;DoubleBuffered=true; Text = $"OpenDesk · {address}"; ClientSize = new(1100, 760); MinimumSize = new(600, 400);
        StartPosition = FormStartPosition.CenterScreen; KeyPreview = true; BackColor = Color.White;
        var top = Theme.Row(state, enableInput); top.Padding = new Padding(12,0,0,0);
        state.MaximumSize=new(360,0);enableInput.Enabled=session.Status.Control;enableInput.Checked=session.Status.Control;
        var disconnect = Theme.Button("Disconnect"); disconnect.Click += (_, _) => Close(); top.Controls.Add(disconnect);
        files=new FileTransfer((message,ct)=>session.SendMessageAsync(message,ct),(offer,ct)=>SessionFiles.ChooseSaveAsync(this,offer,ct));
        files.Notice+=text=>Ui(()=>state.Text=text);
        var chatButton=Theme.Button("Chat");chatButton.Click+=(_,_)=>OpenChat();
        fileButton.Enabled=session.Status.Files;fileButton.Click+=async(_,_)=>{string? path=SessionFiles.PickSend(this);if(path is null)return;fileButton.Enabled=false;try{await files.SendAsync(path,stop.Token);}catch(Exception ex){Ui(()=>state.Text=ex.Message);}finally{fileButton.Enabled=session.Status.Files;}};
        clipboardButton.Enabled=session.Status.Clipboard;clipboardButton.Click+=async(_,_)=>{try{if(Clipboard.ContainsText()){string text=Clipboard.GetText();if(text.Length>2000)throw new IOException("Clipboard text is limited to 2,000 characters.");await session.SendMessageAsync(Wire.Json(MessageKind.Clipboard,text),stop.Token);state.Text="Clipboard text sent";}}catch(Exception ex){state.Text=ex.Message;}};
        var screenshot=Theme.Button("Screenshot");screenshot.Click+=(_,_)=>SaveScreenshot();recordButton.Click+=(_,_)=>ToggleRecording();
        var fullscreen=Theme.Button("Fullscreen");fullscreen.Click+=(_,_)=>ToggleFullscreen();
        top.Controls.AddRange([chatButton,fileButton,clipboardButton,screenshot,recordButton,fullscreen]);
        foreach(var button in top.Controls.OfType<Button>()){button.MinimumSize=new(75,32);button.Padding=new(6,2,6,2);button.Font=new Font("Segoe UI",9);}
        session.MessageReceived=ReceiveMessageAsync;
        var hint = Theme.Label("Click the screen to focus. Ctrl+Alt+F12 releases remote input. Windows secure desktops are unavailable.", 9);
        hint.Dock = DockStyle.Bottom; hint.Height = 30; hint.Padding = new(12,0,0,0);
        Controls.Add(picture); Controls.Add(hint); Controls.Add(top);
        enableInput.CheckedChanged += (_, _) => { if (!enableInput.Checked) Queue(new("reset")); };
        picture.MouseDown += (_, e) => { picture.Focus();if(!InputEnabled){state.Text=session.Status.Control?"Input paused · enable Send keyboard and mouse":"View only · ask the host to allow control";return;}heldButtons |= e.Button; picture.Capture = true; Pointer(e, "button", true); };
        picture.MouseUp += (_, e) => { Pointer(e, "button", false); heldButtons &= ~e.Button; picture.Capture = heldButtons != MouseButtons.None; };
        picture.MouseMove += (_, e) => { long now = Environment.TickCount64; if (now - lastMove >= 16) { lastMove = now; Pointer(e, "move"); } };
        picture.MouseWheel += (_, e) => Pointer(e, "wheel");
        picture.LostFocus += (_, _) => Queue(new("reset"));
        picture.MouseCaptureChanged += (_, _) => { if (!picture.Capture && heldButtons != MouseButtons.None) { heldButtons = MouseButtons.None; Queue(new("reset")); } };
        Deactivate += (_, _) => Queue(new("reset"));
        Shown += (_, _) => { reading = Task.Run(ReadAsync); sending = Task.Run(SendAsync); };
        FormClosing += (_, _) => { stop.Cancel(); inputs.Complete(); };
        FormClosed += async (_, _) => {
            await session.DisposeAsync();
            if (reading is not null) { try { await reading; } catch (Exception) { } }
            if (sending is not null) { try { await sending; } catch (Exception) { } }
            StopRecording();await files.DisposeAsync();chat?.Close();
        };
    }
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Alt | Keys.F12)) { enableInput.Checked = false; Queue(new("reset")); return true; }
        if(keyData==Keys.Escape&&fullScreen&&!enableInput.Checked){ToggleFullscreen();return true;}
        if (picture.Focused && enableInput.Checked && session.Status.Control && msg.Msg is 0x100 or 0x104) {
            Queue(new("key", Code:(int)(keyData & Keys.KeyCode), Down:true)); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (picture.Focused && enableInput.Checked && session.Status.Control) { Queue(new("key", Code:e.KeyValue, Down:true)); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (picture.Focused && enableInput.Checked && session.Status.Control) { Queue(new("key", Code:e.KeyValue, Down:false)); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyUp(e);
    }
    private void Pointer(MouseEventArgs e, string kind, bool down = false)
    {
        if (!enableInput.Checked || !session.Status.Control) return;
        var image = picture.ImageSize;var point = ViewerCoordinates.Normalize(e.X, e.Y, picture.Width, picture.Height, image.Width, image.Height);
        if (point is null) { if (kind == "button" && !down) Queue(new("reset")); return; }
        int button = e.Button switch { MouseButtons.Left => 1, MouseButtons.Right => 2, MouseButtons.Middle => 3, _ => 0 };
        Queue(new(kind, point.Value.X, point.Value.Y, button, down, Delta:e.Delta));
    }
    private void Queue(InputEvent value)
    {
        if (stop.IsCancellationRequested || !session.Status.Control || (value.Kind!="reset"&&Volatile.Read(ref appliedEpoch)!=session.Status.Epoch)) return;
        if (!inputs.Post(value with { Epoch = session.Status.Epoch })) {
            enableInput.Checked = false; stop.Cancel(); Close();
        }
    }
    private async Task SendAsync()
    {
        try { await foreach (var input in inputs.ReadAllAsync(stop.Token)) await session.SendInputAsync(input, stop.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { stop.Cancel(); Ui(() => { state.Text = "Input connection ended"; enableInput.Checked = false; }); }
    }
    private async Task ReadAsync()
    {
        bool hadControl=session.Status.Control;
        try { await session.RunAsync(frame=>Present(()=>picture.Apply(frame),frame.Epoch), status => Ui(() => {
            state.Text = status.Control ? "Connected · You have control" : "Connected · View only";
            enableInput.Enabled = status.Control;if(status.Control&&!hadControl)enableInput.Checked=true;if(!status.Control)enableInput.Checked=false;hadControl=status.Control;
            fileButton.Enabled=status.Files;clipboardButton.Enabled=status.Clipboard;
        }), stop.Token, update=>Present(()=>picture.Apply(update),update.Epoch)); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) {
            Ui(() => { state.Text = "Disconnected · Close this window to reconnect"; enableInput.Enabled = enableInput.Checked = false; });
        }
        finally { stop.Cancel(); await session.DisposeAsync(); }
    }
    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    // Reader thread: decode and paste into the surface, then let the UI repaint only what changed.
    private void Present(Action apply,long epoch)
    {
        if(stop.IsCancellationRequested)return;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try {
            apply();LastDecodeMilliseconds=watch.Elapsed.TotalMilliseconds;
            Volatile.Write(ref appliedEpoch,epoch);ScheduleDisplay();
        } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OutOfMemoryException) {
            stop.Cancel();Ui(()=>state.Text = "Invalid screen frame · disconnected");
        }
    }
    private void ScheduleDisplay(){if(Interlocked.Exchange(ref displayPosted,1)==0)Ui(DisplayFrame);}
    private void DisplayFrame(){
        Interlocked.Exchange(ref displayPosted,0);
        picture.InvalidateChanges();RenderedFrames++;
    }
    // Tile updates arrive only when something changes, so record from the surface at a steady rate.
    private void RecordFrame()
    {
        lock(recordingGate){
            if(recorder is null)return;
            try{if(picture.EncodeFrame() is {} frame)recorder.Add(frame);}
            catch(Exception ex)when(ex is IOException or InvalidOperationException or ArgumentException or ObjectDisposedException){StopRecording();Ui(()=>state.Text="Recording stopped: "+ex.Message);}
        }
    }
    private void OpenChat(){if(chat is null||chat.IsDisposed)chat=new ChatForm(Text,text=>session.SendMessageAsync(Wire.Json(MessageKind.Chat,text),stop.Token));if(!chat.Visible)chat.Show(this);else chat.Activate();}
    private async Task ReceiveMessageAsync(OpenDesk.Core.Message message,CancellationToken ct)
    {
        if(message.Kind is MessageKind.Chat or MessageKind.Clipboard){string text=Wire.Decode<string>(message);if(text.Length>2000)throw new InvalidDataException("Text message is too long.");
            if(message.Kind==MessageKind.Chat)Ui(()=>{OpenChat();chat!.Append("Remote",text);});
            else Ui(()=>{try{Clipboard.SetText(text);state.Text="Received clipboard text";}catch(System.Runtime.InteropServices.ExternalException){state.Text="Clipboard is busy";}});
        }else await files.ReceiveAsync(message,ct);
    }
    private void SaveScreenshot()
    {
        using var image=picture.Snapshot();if(image is null)return;using var dialog=new SaveFileDialog{Filter="PNG image|*.png",FileName="OpenDesk-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".png",OverwritePrompt=true};if(dialog.ShowDialog(this)==DialogResult.OK){try{image.Save(dialog.FileName,System.Drawing.Imaging.ImageFormat.Png);}catch(Exception ex){state.Text=ex.Message;}}
    }
    private void ToggleRecording()
    {
        if(recorder is not null){StopRecording();return;}
        var size=picture.ImageSize;if(size.IsEmpty)return;using var dialog=new SaveFileDialog{Filter="Motion JPEG video|*.avi",FileName="OpenDesk-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".avi",OverwritePrompt=true};if(dialog.ShowDialog(this)==DialogResult.OK){try{lock(recordingGate){recorder=new(dialog.FileName,size.Width,size.Height);recordingTimer??=new(_=>RecordFrame(),null,0,100);}recordButton.Text="Stop recording";state.Text="Recording session…";}catch(Exception ex){state.Text=ex.Message;}}
    }
    private void ToggleFullscreen()
    {
        if(!fullScreen){previousState=WindowState;previousBounds=Bounds;FormBorderStyle=FormBorderStyle.None;WindowState=FormWindowState.Normal;Bounds=Screen.FromControl(this).Bounds;fullScreen=true;}
        else{FormBorderStyle=FormBorderStyle.Sizable;WindowState=FormWindowState.Normal;Bounds=previousBounds;WindowState=previousState;fullScreen=false;}
    }
    private void StopRecording(){lock(recordingGate){var active=recorder;recorder=null;recordingTimer?.Dispose();recordingTimer=null;Ui(()=>recordButton.Text="Record");if(active is null)return;try{active.Dispose();Ui(()=>state.Text="Recording saved");}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Ui(()=>state.Text="Recording could not be finalized: "+ex.Message);}}}
}
