using System.Drawing.Imaging;
using OpenDesk.Core;

namespace OpenDesk.App;

// Viewer surface. The reader thread pastes changed tiles into the remote image and rescales just
// those areas into a window-sized copy, so painting is a plain clipped BitBlt on the UI thread.
internal sealed unsafe class RemoteScreen : Control
{
    private readonly object gate=new();
    private readonly List<Rectangle> dirty=[];
    private Gdi.Dib? canvas,display;
    private Size area;
    private bool whole,closed;
    private Size imageSize;
    internal RemoteScreen(){SetStyle(ControlStyles.Selectable|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.Opaque,true);TabStop=true;}
    protected override bool IsInputKey(Keys keyData)=>true;
    // Read without the lock: mouse handling must not wait for the reader thread.
    internal Size ImageSize=>imageSize;
    protected override void OnSizeChanged(EventArgs e){lock(gate)area=ClientSize;Invalidate();base.OnSizeChanged(e);}

    internal void Apply(ScreenFrame frame)=>Paste(frame.Jpeg,frame.Width,frame.Height,frame.Width,frame.Height,[(new(0,0,frame.Width,frame.Height),Point.Empty)]);
    internal void Apply(TileUpdate update)
    {
        if(update.Tiles.Length==0){lock(gate)Canvas(update.Width,update.Height);return;}
        var (columns,rows)=update.Atlas;var parts=new (Rectangle,Point)[update.Tiles.Length];
        for(int i=0;i<parts.Length;i++){var s=update.Slot(i);parts[i]=(new(s.AtlasX,s.AtlasY,s.Width,s.Height),new(s.X,s.Y));}
        Paste(update.Jpeg,columns*update.TileSize,rows*update.TileSize,update.Width,update.Height,parts);
    }
    // Decodes one JPEG and copies each source rectangle of it into the image at its target point.
    private void Paste(byte[] jpeg,int jpegWidth,int jpegHeight,int width,int height,(Rectangle From,Point To)[] parts)
    {
        if(JpegDimensions.Read(jpeg)!=(jpegWidth,jpegHeight))throw new InvalidDataException("Frame size mismatch.");
        using var stream=new MemoryStream(jpeg);using var image=(Bitmap)Image.FromStream(stream,false,false);
        var data=image.LockBits(new Rectangle(0,0,jpegWidth,jpegHeight),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
        try{
            lock(gate){
                var target=Canvas(width,height);var rows=new Dictionary<int,Rectangle>();
                foreach(var (from,to) in parts){
                    byte* source=(byte*)data.Scan0+from.Y*data.Stride+from.X*4,destination=(byte*)target.Bits+to.Y*target.Stride+to.X*4;
                    for(int r=0;r<from.Height;r++)Buffer.MemoryCopy(source+r*data.Stride,destination+r*target.Stride,from.Width*4,from.Width*4);
                    var changed=new Rectangle(to,from.Size);rows[to.Y]=rows.TryGetValue(to.Y,out var row)?Rectangle.Union(row,changed):changed;
                }
                // Rescale per changed tile row, or the whole changed span when many rows changed.
                List<Rectangle> areas=rows.Count>4?[rows.Values.Aggregate(Rectangle.Union)]:[..rows.Values];
                if(Display(out var placed,out bool rebuilt) is {} shown&&!rebuilt)foreach(var changed in areas){
                    var scaled=Resample.Affected(changed,target.Width,target.Height,shown.Width,shown.Height);
                    Resample.Bilinear((byte*)target.Bits,target.Stride,target.Width,target.Height,(byte*)shown.Bits,shown.Stride,shown.Width,shown.Height,scaled);
                    scaled.Offset(placed.Location);dirty.Add(scaled);
                    if(dirty.Count>64){var all=dirty.Aggregate(Rectangle.Union);dirty.Clear();dirty.Add(all);}
                }
            }
        }finally{image.UnlockBits(data);}
    }
    private Gdi.Dib Canvas(int width,int height)
    {
        if(closed)throw new ObjectDisposedException(nameof(RemoteScreen));
        if(canvas is null||canvas.Width!=width||canvas.Height!=height){canvas?.Dispose();canvas=new(width,height);imageSize=new(width,height);display?.Dispose();display=null;whole=true;dirty.Clear();}
        return canvas;
    }
    // The image scaled to fit the window (ViewerCoordinates.Fit, as clicks are mapped); rebuilt after a resize.
    private Gdi.Dib? Display(out Rectangle placed,out bool rebuilt)
    {
        placed=Rectangle.Empty;rebuilt=false;if(canvas is null||area.Width<1||area.Height<1)return null;
        var (x,y,w,h)=ViewerCoordinates.Fit(area.Width,area.Height,canvas.Width,canvas.Height);placed=new(x,y,w,h);
        if(display is null||display.Width!=w||display.Height!=h){
            display?.Dispose();display=new(w,h);
            Resample.Bilinear((byte*)canvas.Bits,canvas.Stride,canvas.Width,canvas.Height,(byte*)display.Bits,display.Stride,w,h);
            whole=rebuilt=true;
        }
        return display;
    }
    // UI thread: repaint what changed since the last call.
    internal void InvalidateChanges()
    {
        Rectangle[] changed;bool all;
        lock(gate){changed=dirty.ToArray();dirty.Clear();all=whole;whole=false;}
        if(all){Invalidate();return;}
        foreach(var r in changed){r.Inflate(1,1);Invalidate(r);}
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        lock(gate){
            var shown=Display(out var placed,out _);
            using(var bars=new Region(ClientRectangle))using(var brush=new SolidBrush(BackColor)){bars.Exclude(placed);e.Graphics.FillRegion(brush,bars);}
            if(shown is null)return;
            nint dc=e.Graphics.GetHdc();
            try{Gdi.BitBlt(dc,placed.X,placed.Y,placed.Width,placed.Height,shown.Dc,0,0,Gdi.SrcCopy);}finally{e.Graphics.ReleaseHdc(dc);}
        }
    }
    internal Color Pixel(int x,int y){lock(gate){if(canvas is null||(uint)x>=canvas.Width||(uint)y>=canvas.Height)return Color.Empty;uint v=((uint*)canvas.Bits)[y*canvas.Width+x];return Color.FromArgb(255,Color.FromArgb((int)v));}}
    internal Bitmap? Snapshot(){lock(gate){if(canvas is null)return null;using var view=new Bitmap(canvas.Width,canvas.Height,canvas.Stride,PixelFormat.Format32bppRgb,canvas.Bits);return new Bitmap(view);}}
    // Copies under the lock, encodes outside it, so recording never stalls drawing.
    internal ScreenFrame? EncodeFrame(){using var copy=Snapshot();return copy is null?null:new(copy.Width,copy.Height,ScreenCapture.EncodeJpeg(copy,80));}
    protected override void Dispose(bool disposing){if(disposing)lock(gate){closed=true;canvas?.Dispose();display?.Dispose();canvas=display=null;imageSize=Size.Empty;}base.Dispose(disposing);}
}
