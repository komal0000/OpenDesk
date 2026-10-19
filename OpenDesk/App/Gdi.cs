using System.Runtime.InteropServices;

namespace OpenDesk.App;

internal static class Gdi
{
    internal const int SrcCopy=0xCC0020;
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(nint dst,int x,int y,int w,int h,nint src,int sx,int sy,int rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc,ref BitmapInfoHeader info,uint usage,out nint bits,nint section,uint offset);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader{public int Size,Width,Height;public short Planes,BitCount;public int Compression,SizeImage,XPelsPerMeter,YPelsPerMeter,ClrUsed,ClrImportant;}

    // Top-down 32-bit DIB selected into its own memory DC: GDI can blit it and code can write its pixels.
    internal sealed class Dib:IDisposable
    {
        internal nint Dc{get;}
        internal nint Bits{get;}
        internal int Width{get;}
        internal int Height{get;}
        internal int Stride=>Width*4;
        private readonly nint bitmap,previous;
        internal Dib(int width,int height){
            var info=new BitmapInfoHeader{Size=40,Width=width,Height=-height,Planes=1,BitCount=32};
            Dc=CreateCompatibleDC(0);bitmap=CreateDIBSection(Dc,ref info,0,out var bits,0,0);
            if(Dc==0||bitmap==0){if(Dc!=0)DeleteDC(Dc);throw new OutOfMemoryException("Could not allocate a screen buffer.");}
            Bits=bits;Width=width;Height=height;previous=SelectObject(Dc,bitmap);
        }
        // GDI read-back of a screen area (about 30 ms for a full monitor, but works everywhere).
        internal void CopyFromScreen(Rectangle area){nint screen=GetDC(0);try{BitBlt(Dc,0,0,area.Width,area.Height,screen,area.X,area.Y,SrcCopy);GdiFlush();}finally{ReleaseDC(0,screen);}}
        public void Dispose(){SelectObject(Dc,previous);DeleteObject(bitmap);DeleteDC(Dc);}
    }
}
