using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using OpenDesk.Core;

namespace OpenDesk.App;

// One v2 session to a remote device. This form runs signaling and checks the host's signed answer,
// then the viewer page carries video and input over WebRTC; the form handles status, chat, clipboard,
// files, screenshots and recording.
internal sealed class ViewerForm : Form
{
    private const long MaxRecording = 1_500_000_000;
    private readonly ClientSettings settings;
    private readonly string id;
    private readonly Func<bool> locallyControlled;
    private readonly WebView2 page = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(31, 32, 35) };
    private readonly ChannelBridge bridge;
    private readonly PeerViewer peer;
    private readonly FileTransfer files;
    private readonly CancellationTokenSource stop = new();
    private readonly Inbox inbox;
    private readonly Label state = Theme.Label("Connecting…", 10, true);
    private readonly CheckBox enableInput = new() { Text = "Send keyboard and mouse", AutoSize = true, Enabled = false, Margin = new Padding(12, 12, 12, 4) };
    private readonly ToolStripButton fileButton = new("Send file"), clipboardButton = new("Send clipboard"), recordButton = new("Record"), statsButton = new("Stats");
    private TaskCompletionSource<string>? offer;
    private TaskCompletionSource<byte[]>? screenshot;
    private FileStream? recording;
    private ChatForm? chat;
    private bool connected, hadControl, fullScreen, overlay;
    private string route = "";
    private FormWindowState previousState;
    private Rectangle previousBounds;
    internal int RenderedFrames { get; private set; }
    internal bool InputEnabled => enableInput.Checked && peer.Status.Control;
    internal WebView2 Page => page;

    // locallyControlled: someone controls this PC right now. Their injected input could reach this window
    // (the WinForms input filter cannot see into WebView2), so input to the remote device pauses meanwhile.
    internal ViewerForm(ClientSettings settings, string id, Func<bool> locallyControlled, bool autoConnect = true)
    {
        SuspendLayout();
        this.settings = settings; this.id = id; this.locallyControlled = locallyControlled;
        bridge = new(page);
        peer = new((channel, data, ct) => bridge.SendAsync(null, channel, data, ct));
        files = new FileTransfer(peer.SendMessageAsync, (fileOffer, ct) => SessionFiles.ChooseSaveAsync(this, fileOffer, ct));
        files.Notice += text => Ui(() => state.Text = text);
        peer.MessageReceived = ReceiveMessageAsync;
        peer.StatusChanged += status => Ui(() => ApplyStatus(status));
        inbox = new(peer.ReceiveAsync, ex => Ui(() => Fail(ex is UnauthorizedAccessException ? ex.Message : "The host sent invalid data; the session was closed.")), stop.Token);
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
        Text = $"OpenDesk · {DeviceProof.FormatId(id)}"; ClientSize = new(1100, 760); MinimumSize = new(600, 400);
        StartPosition = FormStartPosition.CenterScreen; KeyPreview = true; BackColor = page.DefaultBackgroundColor;Font=new Font("Segoe UI",10);
        var top = Theme.Row(state, enableInput);top.BackColor=Color.White;top.Dock=DockStyle.Top;top.Padding = new Padding(12, 0, 0, 0);
        state.MaximumSize = new(420, 0);
        var disconnect = new ToolStripButton("Disconnect"){ForeColor=Theme.Accent};disconnect.Click += (_, _) => Close();
        var chatButton = new ToolStripButton("Chat");chatButton.Click += (_, _) => OpenChat();
        fileButton.Enabled = clipboardButton.Enabled = false;
        fileButton.Click += async (_, _) => {
            string? path = SessionFiles.PickSend(this); if (path is null) return;
            fileButton.Enabled = false;
            try { await files.SendAsync(path, stop.Token); } catch (Exception ex) { state.Text = ex.Message; } finally { fileButton.Enabled = peer.Status.Files; }
        };
        clipboardButton.Click += async (_, _) => {
            try {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText();
                if (text.Length > 2000) throw new IOException("Clipboard text is limited to 2,000 characters.");
                await peer.SendMessageAsync(Wire.Json(MessageKind.Clipboard, text), stop.Token); state.Text = "Clipboard text sent";
            } catch (Exception ex) { state.Text = ex.Message; }
        };
        var screenshotButton = new ToolStripButton("Screenshot");screenshotButton.Click += async (_, _) => await SaveScreenshotAsync();
        recordButton.Click += (_, _) => ToggleRecording();
        var fullscreen = new ToolStripButton("Fullscreen");fullscreen.Click += (_, _) => ToggleFullscreen();
        statsButton.Click += (_, _) => { overlay = !overlay; Post(new { t = "overlay", on = overlay }); };
        var toolbar=new ToolStrip {Name="SessionToolbar",Dock=DockStyle.Top,GripStyle=ToolStripGripStyle.Hidden,CanOverflow=true,AutoSize=false,Height=44,Padding=new Padding(8,4,8,4),BackColor=Theme.Sidebar,Renderer=new ToolStripSystemRenderer(),LayoutStyle=ToolStripLayoutStyle.HorizontalStackWithOverflow};
        toolbar.Items.AddRange([chatButton,fileButton,clipboardButton,new ToolStripSeparator(),screenshotButton,recordButton,new ToolStripSeparator(),fullscreen,statsButton,new ToolStripSeparator(),disconnect]);
        foreach(var button in toolbar.Items.OfType<ToolStripButton>()){button.Font=new Font("Segoe UI",9);button.Padding=new(8,4,8,4);button.DisplayStyle=ToolStripItemDisplayStyle.Text;button.Overflow=ToolStripItemOverflow.AsNeeded;}
        var hint = Theme.Label("Click the screen to focus. Ctrl+Alt+F12 releases remote input. Windows secure desktops are unavailable.", 9);
        hint.Dock=DockStyle.Bottom;hint.AutoSize=false;hint.Height=44;hint.Padding=new(12,4,12,4);hint.ForeColor=Theme.Muted;hint.BackColor=Color.White;
        enableInput.AccessibleName="Send keyboard and mouse";
        Controls.Add(page);Controls.Add(hint);Controls.Add(toolbar);Controls.Add(top);
        enableInput.CheckedChanged += (_, _) => PostStatus();
        Shown += async (_, _) => {if(autoConnect)await ConnectAsync();};
        FormClosing += (_, _) => { stop.Cancel(); inbox.Complete(); Post(new { t = "close" }); };
        FormClosed += async (_, _) => { StopRecording(); bridge.FailAll(); await files.DisposeAsync(); chat?.Close(); page.Dispose(); };
        ResumeLayout(true);
    }

    private async Task ConnectAsync()
    {
        try {
            if (!Signal.ValidName(settings.DisplayName)) throw new FormatException("Set a display name of 1–64 characters in Settings.");
            state.Text = $"Finding {DeviceProof.FormatId(id)}…";
            var opening = WebEngine.OpenAsync(page, "viewer.html", OnPage);
            await using var signaling = await SignalingClient.ConnectAsync(settings.ServiceUrl, settings.ServicePin, "v2/connect/" + id, stop.Token);
            var ready = await signaling.ReceiveAsync(TimeSpan.FromSeconds(15), stop.Token);
            if (ready is not { Type: "ready", Session: { } session, PublicKey: { } publicKey }) throw new IOException("Unexpected device service response.");
            // Trust on first use: the device key saved for this address must not change silently.
            string keyPin = SignalProof.Fingerprint(publicKey);
            if (settings.KnownKeys.TryGetValue(id, out var known) && known != keyPin)
                throw new UnauthorizedAccessException("This address now belongs to a different device key. If that computer was reinstalled, remove it from your address book and connect again. Otherwise, do not continue.");
            await opening;
            offer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(new { t = "start", ice = ready.Ice ?? [] });
            string sdp = await offer.Task.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            await signaling.SendAsync(new("offer", Name: settings.DisplayName.Trim(), Sdp: sdp), stop.Token);
            Show("Waiting for the remote host to approve…");
            var reply = await signaling.ReceiveAsync(TimeSpan.FromSeconds(95), stop.Token);
            if (reply.Type == "reject") throw new UnauthorizedAccessException(reply.Reason ?? "The host declined the request.");
            // The signature binds both descriptions, and so both encryption fingerprints, to the host's device key.
            if (reply is not { Type: "answer", Sdp: { } answer } || !SignalProof.Verify(publicKey, SignalProof.Answer(id, session, sdp, answer), reply.Signature))
                throw new UnauthorizedAccessException("The device's answer was not signed by its key, so the connection was stopped.");
            Post(new { t = "answer", sdp = answer });
            Show("Approved · connecting…");
            var status = await peer.Accepted.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            connected = true;
            settings.Remember(id, DeviceProof.FormatId(id), keyPin);
            ApplyStatus(status);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        // The window's only job here is to say why the session did not start.
        catch (Exception ex) {
            Fail(ex is TimeoutException ? "The connection timed out. Both devices must be online; on different networks the device service needs TURN." : ex.Message);
        }
    }

    // Messages from the viewer page (UI thread).
    private void OnPage(JsonElement m)
    {
        if (bridge.TryAcknowledge(m)) return;
        switch (m.GetProperty("t").GetString()) {
            case "offer": offer?.TrySetResult(m.GetProperty("sdp").GetString() ?? ""); break;
            case "recv": if (!inbox.Post(m)) Fail("The host sent data too fast; the session was closed."); break;
            case "frame": RenderedFrames++; break;
            case "state":
                string connection = m.GetProperty("state").GetString() ?? "";
                if (connection is "failed" or "closed" && connected) Fail("Disconnected · close this window and connect again.");
                else if (connection == "disconnected" && connected) state.Text = "Connection interrupted · waiting for the network…";
                else if (connection == "connected" && connected) ApplyStatus(peer.Status);
                break;
            case "route": route = m.GetProperty("relay").GetBoolean() ? " · relayed" : " · direct"; break;
            case "release": enableInput.Checked = false; break;
            case "escape": if (fullScreen) ToggleFullscreen(); break;
            case "hint": state.Text = peer.Status.Control ? "Input paused · enable Send keyboard and mouse" : "View only · ask the host to allow control"; break;
            case "screenshot": screenshot?.TrySetResult(m.GetProperty("data").GetBytesFromBase64()); break;
            case "recording": WriteRecording(m.GetProperty("data").GetBytesFromBase64()); break;
            case "recorded": FinishRecording(); break;
            case "failed": Fail("The session could not start: " + m.GetProperty("message").GetString()); break;
        }
    }

    private void ApplyStatus(SessionStatus status)
    {
        if (!connected) return;
        state.Text = (status.Control ? "Connected · You have control" : "Connected · View only") + route;
        enableInput.Enabled = status.Control;
        if (status.Control && !hadControl) enableInput.Checked = true;
        if (!status.Control) enableInput.Checked = false;
        hadControl = status.Control;
        fileButton.Enabled = status.Files; clipboardButton.Enabled = status.Clipboard;
        PostStatus();
    }
    private void PostStatus() { var s = peer.Status; Post(new { t = "status", control = s.Control, epoch = s.Epoch, display = s.Display, input = enableInput.Checked && connected && !locallyControlled() }); }
    // Called when this PC's own remote control changes.
    internal void RefreshInput() { if (connected) { PostStatus(); if (locallyControlled() && enableInput.Checked) state.Text = "Input paused while this PC is being controlled remotely"; } }
    private void Show(string text) { state.Text = text; Post(new { t = "message", text }); }
    private void Fail(string text)
    {
        connected = false; enableInput.Checked = enableInput.Enabled = false; fileButton.Enabled = clipboardButton.Enabled = false;
        Show(text); Post(new { t = "close" }); stop.Cancel();
    }
    private void Post(object message) => WebEngine.Post(page, message);
    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }

    private void OpenChat()
    {
        if (chat is null || chat.IsDisposed) chat = new ChatForm(Text, text => peer.SendMessageAsync(Wire.Json(MessageKind.Chat, text), stop.Token));
        if (!chat.Visible) chat.Show(this); else chat.Activate();
    }
    private async Task ReceiveMessageAsync(OpenDesk.Core.Message message, CancellationToken ct)
    {
        if (message.Kind is MessageKind.Chat or MessageKind.Clipboard) {
            string text = Wire.Decode<string>(message);
            if (message.Kind == MessageKind.Chat) Ui(() => { OpenChat(); chat!.Append("Remote", text); });
            else Ui(() => { try { Clipboard.SetText(text); state.Text = "Received clipboard text"; } catch (System.Runtime.InteropServices.ExternalException) { state.Text = "Clipboard is busy"; } });
        }
        else await files.ReceiveAsync(message, ct);
    }

    private async Task SaveScreenshotAsync()
    {
        if (!connected) return;
        screenshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new { t = "screenshot" });
        byte[] png;
        try { png = await screenshot.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { state.Text = "Screenshot failed"; return; }
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "OpenDesk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { try { await File.WriteAllBytesAsync(dialog.FileName, png); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state.Text = ex.Message; } }
    }
    private void ToggleRecording()
    {
        if (recording is not null) { Post(new { t = "record", on = false }); recordButton.Text = "Record"; return; }
        if (!connected) return;
        using var dialog = new SaveFileDialog { Filter = "WebM video|*.webm", FileName = "OpenDesk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".webm", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { recording = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state.Text = ex.Message; return; }
        Post(new { t = "record", on = true }); recordButton.Text = "Stop recording"; state.Text = "Recording session…";
    }
    private void WriteRecording(byte[] chunk)
    {
        if (recording is null) return;
        try {
            recording.Write(chunk);
            if (recording.Length > MaxRecording) { Post(new { t = "record", on = false }); recordButton.Text = "Record"; state.Text = "Recording stopped at the 1.5 GB limit"; }
        }
        catch (IOException ex) { Post(new { t = "record", on = false }); StopRecording(); state.Text = "Recording stopped: " + ex.Message; }
    }
    private void FinishRecording() { StopRecording(); state.Text = "Recording saved"; }
    private void StopRecording() { recording?.Dispose(); recording = null; recordButton.Text = "Record"; }
    private void ToggleFullscreen()
    {
        if (!fullScreen) { previousState = WindowState; previousBounds = Bounds; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds; fullScreen = true; }
        else { FormBorderStyle = FormBorderStyle.Sizable; WindowState = FormWindowState.Normal; Bounds = previousBounds; WindowState = previousState; fullScreen = false; }
    }
}
