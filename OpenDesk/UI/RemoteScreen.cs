using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenDesk.Core;

namespace OpenDesk.UI;

// The remote screen: the newest decoded picture scaled to fit (letterboxed), and the pointer and keyboard
// input over it, as normalized InputEvents. Input flows only while ActiveEpoch returns an epoch.
public sealed class RemoteScreen : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(31, 32, 35));
    private WriteableBitmap? bitmap;
    private long shown;
    private (double X, double Y)? pendingMove;
    // The display generation of the picture on screen (-1 before the first picture).
    public long ShownDisplay { get; private set; } = -1;
    public int PictureWidth => bitmap?.PixelSize.Width ?? 0;
    public int PictureHeight => bitmap?.PixelSize.Height ?? 0;
    public long FramesShown { get; private set; }
    // The status epoch to send input with, or null while input must not flow.
    public Func<long?> ActiveEpoch { get; set; } = () => null;
    public Func<InputEvent, bool> Send { get; set; } = _ => false;
    // Clicked while input is off; Escape while input is off; Ctrl+Alt+F12.
    public event Action? Hint, EscapePressed, ReleaseRequested;
    public event Action? FirstPicture;

    public RemoteScreen() { Focusable = true; ClipToBounds = true; Cursor = new Cursor(StandardCursorType.Arrow); }

    // UI thread: shows the presenter's newest picture, if any.
    public void Present(VideoPresenter presenter)
    {
        bool first = bitmap is null;
        shown = presenter.CopyLatest(shown, (width, height, stride, pixels) => {
            if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height) {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var target = bitmap.Lock();
            unsafe {
                for (int y = 0; y < height; y++)
                    pixels.Slice(y * stride, width * 4).CopyTo(new Span<byte>((byte*)target.Address + (long)y * target.RowBytes, width * 4));
            }
            FramesShown++;
        }, out long display);
        ShownDisplay = display;
        if (first && bitmap is not null) FirstPicture?.Invoke();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (bitmap is not null && Fit() is { } area)
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), area);
    }

    private Rect? Fit()
    {
        if (bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        double w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height, scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        return new Rect((Bounds.Width - w * scale) / 2, (Bounds.Height - h * scale) / 2, w * scale, h * scale);
    }
    // Position inside the picture, 0..1, or null outside it (letterboxing excluded).
    private (double X, double Y)? Normalize(Point p)
    {
        if (Fit() is not { } area) return null;
        double x = (p.X - area.X) / area.Width, y = (p.Y - area.Y) / area.Height;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? (x, y) : null;
    }
    private void Emit(string kind, (double X, double Y)? at = null, int code = 0, bool down = false, int delta = 0, string? key = null)
    {
        if (ActiveEpoch() is not { } epoch) return;
        var (x, y) = at ?? (0, 0);
        Send(new InputEvent(kind, x, y, code, down, epoch, delta, key));
    }
    // Releases every key and button held on the host.
    public void Reset() => Emit("reset");

    private static int ButtonCode(PointerUpdateKind kind) => kind switch {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
        _ => 0 };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(); e.Handled = true;
        if (ActiveEpoch() is null) { Hint?.Invoke(); return; }
        var point = e.GetCurrentPoint(this);
        if (Normalize(point.Position) is { } at && ButtonCode(point.Properties.PointerUpdateKind) is > 0 and var code) Emit("button", at, code, true);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled = true;
        var point = e.GetCurrentPoint(this);
        if (Normalize(point.Position) is not { } at) Reset();
        else if (ButtonCode(point.Properties.PointerUpdateKind) is > 0 and var code) Emit("button", at, code, false);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); Reset(); }
    // Moves are coalesced to one per rendered frame.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Normalize(e.GetPosition(this)) is not { } at) return;
        bool scheduled = pendingMove is not null;
        pendingMove = at;
        if (scheduled) return;
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(_ => { if (pendingMove is { } move) Emit("move", move); pendingMove = null; });
        else { Emit("move", at); pendingMove = null; }
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        int delta = (int)Math.Clamp(Math.Round(e.Delta.Y * 120), -1200, 1200);
        if (delta != 0 && Normalize(e.GetPosition(this)) is { } at) Emit("wheel", at, delta: delta);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.F12) {
            e.Handled = true; Reset(); ReleaseRequested?.Invoke(); return;
        }
        if (ActiveEpoch() is null) { if (e.Key == Key.Escape) EscapePressed?.Invoke(); base.OnKeyDown(e); return; }
        e.Handled = true; // Tab, arrows and shortcuts belong to the remote desktop now.
        if (KeyCodes.ToCode(e.PhysicalKey) is { } code) Emit("key", down: true, key: code);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (ActiveEpoch() is null) { base.OnKeyUp(e); return; }
        e.Handled = true;
        if (KeyCodes.ToCode(e.PhysicalKey) is { } code) Emit("key", down: false, key: code);
    }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { base.OnLostFocus(e); Reset(); }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); bitmap?.Dispose(); bitmap = null; }
}
