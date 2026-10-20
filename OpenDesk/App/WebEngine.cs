using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OpenDesk.Core;

namespace OpenDesk.App;

// The app's WebRTC pages run in one shared WebView2 environment and are served from embedded
// resources at a fixed origin. Nothing else can load: other navigation, new windows and permission
// prompts are refused, and page messages from any other origin are ignored.
internal static class WebEngine
{
    internal const string Origin = "https://app.opendesk";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    // Message kind numbers for the pages' framing, from the one definition in Core.
    private static readonly string Kinds = "const MessageKinds = " + JsonSerializer.Serialize(Enum.GetValues<MessageKind>().ToDictionary(k => k.ToString(), k => (int)k)) + ";";
    private static Task<CoreWebView2Environment>? environment;

    private static Task<CoreWebView2Environment> EnvironmentAsync() => environment ??= CoreWebView2Environment.CreateAsync(null,
        Path.Combine(ClientSettings.Folder, "WebView2"),
        // A hidden or minimized host page must keep full speed.
        new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"));

    // Loads an app page into `view`; `received` runs on the UI thread for each message the page posts,
    // and must not keep the element after it returns. Failures surface as IOException.
    internal static async Task OpenAsync(WebView2 view, string page, Action<JsonElement> received)
    {
        CoreWebView2Environment env;
        try { env = await EnvironmentAsync(); }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException) {
            environment = null;
            throw new IOException("OpenDesk needs the Microsoft Edge WebView2 Runtime. Install it from Microsoft, then try again.");
        }
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false; settings.AreDefaultContextMenusEnabled = false; settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false; settings.IsZoomControlEnabled = false; settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false; settings.IsGeneralAutofillEnabled = false; settings.IsPasswordAutosaveEnabled = false;
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => {
            string name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            var content = typeof(WebEngine).Assembly.GetManifestResourceStream("web/" + name);
            e.Response = content is null ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
                : env.CreateWebResourceResponse(content, 200, "OK", "Content-Type: " + (name.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/html") + "; charset=utf-8");
        };
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.WebMessageReceived += (_, e) => {
            if (!e.Source.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
            JsonDocument document;
            try { document = JsonDocument.Parse(e.WebMessageAsJson); } catch (JsonException) { return; }
            using (document) {
                var message = document.RootElement;
                if (message.TryGetProperty("t", out var type) && type.GetString() == "ready") ready.TrySetResult();
                else received(message);
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Kinds);
        core.Navigate($"{Origin}/{page}");
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { throw new IOException("The session page did not start."); }
    }

    // Safe from any thread; dropped once the view is gone. byte[] values are sent as base64.
    internal static void Post(WebView2 view, object message) => Post(view, JsonSerializer.Serialize(message, Web));
    internal static void Post(WebView2 view, string json)
    {
        if (view.IsDisposed) return;
        if (view.InvokeRequired) { try { view.BeginInvoke(() => Post(view, json)); } catch (InvalidOperationException) { } return; }
        view.CoreWebView2?.PostWebMessageAsJson(json);
    }
}

// Sends packed messages through a page's data channels. Each send completes when the page reports the
// channel drained below 1 MB, or fails when the channel is closed, so a 100 MiB file cannot pile up inside
// the page and a lost session surfaces as IOException on every channel.
internal sealed class ChannelBridge(WebView2 view)
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> waiting = new();
    private long next;
    internal async Task SendAsync(string? session, string channel, byte[] data, CancellationToken ct)
    {
        long ack = Interlocked.Increment(ref next);
        var sent = waiting[ack] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebEngine.Post(view, new { t = "send", session, ch = channel, data, ack });
        try { await sent.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { throw new IOException("The connection stopped accepting data."); }
        finally { waiting.TryRemove(ack, out _); }
    }
    // Handles the page's "sent" message; false for any other message.
    internal bool TryAcknowledge(JsonElement message)
    {
        if (message.GetProperty("t").GetString() != "sent") return false;
        long ack = message.GetProperty("ack").GetInt64();
        bool failed = message.TryGetProperty("failed", out var flag) && flag.GetBoolean();
        if (waiting.TryRemove(ack, out var sent)) { if (failed) sent.TrySetException(new IOException("The connection closed.")); else sent.TrySetResult(); }
        return true;
    }
    internal void FailAll() { foreach (var ack in waiting.Keys) if (waiting.TryRemove(ack, out var sent)) sent.TrySetException(new IOException("The connection closed.")); }
}

// One peer's channel messages, handled one at a time in arrival order (file chunks are written in
// sequence). Post returns false when the peer floods it; the caller then ends that session.
internal sealed class Inbox
{
    private readonly Channel<(string Channel, byte[] Data)> queue = Channel.CreateBounded<(string, byte[])>(256);
    internal Inbox(Func<string, byte[], CancellationToken, Task> handle, Action<Exception> failed, CancellationToken ct) => _ = Task.Run(async () => {
        try { await foreach (var (channel, data) in queue.Reader.ReadAllAsync(ct)) await handle(channel, data, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { failed(ex); }
    });
    // A page "recv" message.
    internal bool Post(JsonElement message) => queue.Writer.TryWrite((message.GetProperty("ch").GetString() ?? "", message.GetProperty("data").GetBytesFromBase64()));
    internal void Complete() => queue.Writer.TryComplete();
}
