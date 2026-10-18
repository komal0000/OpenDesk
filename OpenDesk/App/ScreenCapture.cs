using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class ScreenCapture : IDisposable
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly Rectangle bounds;
    private readonly int maxWidth;
    private readonly object gate=new();
    private Bitmap? full,scaled;
    private Graphics? copy,resize;
    private readonly MemoryStream buffer=new(256*1024);
    private readonly EncoderParameters parameters=new(1);
    private byte[] pixels=[];
    private byte[]? previousHash;
    private ScreenFrame? previousFrame;
    private bool disposed;
    internal ScreenCapture(Rectangle bounds,int maxWidth=1280,long quality=65){this.bounds=bounds;this.maxWidth=maxWidth;parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality);}
    public ScreenFrame Capture()
    {
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            if(bounds.Width<1||bounds.Height<1||(long)bounds.Width*bounds.Height>80_000_000)throw new InvalidOperationException("Unsupported monitor size.");
            if(full is null){
                full=new(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb);copy=Graphics.FromImage(full);
                double scale=Math.Min(1,Math.Min((double)maxWidth/bounds.Width,8192.0/bounds.Height));
                scaled=new(Math.Max(1,(int)(bounds.Width*scale)),Math.Max(1,(int)(bounds.Height*scale)),PixelFormat.Format24bppRgb);
                resize=Graphics.FromImage(scaled);resize.InterpolationMode=InterpolationMode.Bilinear;resize.CompositingMode=CompositingMode.SourceCopy;resize.PixelOffsetMode=PixelOffsetMode.HighSpeed;
            }
            copy!.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
            Point cursor=Cursor.Position;
            if(bounds.Contains(cursor)){cursor.Offset(-bounds.X,-bounds.Y);Cursors.Default.Draw(copy,new Rectangle(cursor,Cursors.Default.Size));}
            resize!.DrawImage(full,new Rectangle(Point.Empty,scaled!.Size));
            var data=scaled.LockBits(new Rectangle(Point.Empty,scaled.Size),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            byte[] hash;
            try{int length=Math.Abs(data.Stride)*scaled.Height;if(pixels.Length!=length)pixels=new byte[length];Marshal.Copy(data.Scan0,pixels,0,length);hash=SHA256.HashData(pixels);}
            finally{scaled.UnlockBits(data);}
            if(previousHash is not null&&hash.AsSpan().SequenceEqual(previousHash))return previousFrame!;
            buffer.Position=0;buffer.SetLength(0);scaled.Save(buffer,Jpeg,parameters);
            previousHash=hash;return previousFrame=new(scaled.Width,scaled.Height,buffer.ToArray());
        }
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;copy?.Dispose();resize?.Dispose();full?.Dispose();scaled?.Dispose();buffer.Dispose();parameters.Dispose();}}
}
