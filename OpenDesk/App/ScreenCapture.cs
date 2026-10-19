using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using OpenDesk.Core;

namespace OpenDesk.App;

// Captures one monitor into a scaled image and records the sequence in which each 32-px tile last
// changed, so every viewer is sent only the tiles it does not already hold.
internal sealed unsafe class ScreenCapture : IScreenSource, IDisposable
{
    internal const int Tile = 32;
    internal static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly Rectangle bounds;
    private readonly int width,height,columns,rows;
    private readonly long quality,start;
    private readonly object gate=new();
    private readonly long[] versions;
    private readonly bool[] touchedTiles;
    private readonly List<Rectangle> areas=[],touched=[];
    // clean: scaled desktop; work: clean plus cursor, kept current in place; current: what viewers are sent.
    private readonly byte[] clean,work,current,atlas;
    private Gdi.Dib? grab;
    private DesktopDuplication? duplication;
    private bool duplicated;
    private long retryDuplication,retryDelay=3000,sequence,frameSequence=-1;
    private Point lastCursor;
    private uint[]? cursorPatch;
    private ScreenFrame? frame;
    private bool disposed;
    internal long Sequence{get{lock(gate)return sequence;}}
    internal ScreenCapture(Rectangle bounds,int maxWidth=1280,long quality=65,long startSequence=0)
    {
        if(bounds.Width<1||bounds.Height<1||(long)bounds.Width*bounds.Height>80_000_000)throw new InvalidOperationException("Unsupported monitor size.");
        this.bounds=bounds;this.quality=quality;start=sequence=startSequence;
        double scale=Math.Min(1,Math.Min((double)maxWidth/bounds.Width,8192.0/bounds.Height));
        // Multiples of 16 keep JPEG blocks inside tiles, so packed tiles never bleed into each other.
        width=Math.Max(16,(int)(bounds.Width*scale)/16*16);height=Math.Max(16,(int)(bounds.Height*scale)/16*16);
        columns=(width+Tile-1)/Tile;rows=(height+Tile-1)/Tile;versions=new long[columns*rows];touchedTiles=new bool[columns*rows];
        clean=Pinned(width*height*4);work=Pinned(width*height*4);current=Pinned(width*height*4);atlas=Pinned(columns*rows*Tile*Tile*4);
    }
    private static byte[] Pinned(int size)=>GC.AllocateArray<byte>(size,pinned:true);
    private static nint Address(byte[] pinned)=>(nint)Unsafe.AsPointer(ref pinned[0]);
    public long Capture()
    {
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            bool first=sequence==start,image=Grab();Point cursor=Cursor.Position;
            if(!image&&cursor==lastCursor&&!first)return sequence;
            // Only areas the desktop or the cursor touched can differ: refresh and compare just those.
            if(first){touched.Clear();touched.Add(new(0,0,width,height));}
            touched.Add(CursorBox(lastCursor));touched.Add(CursorBox(cursor));lastCursor=cursor;
            int stride=width*4;
            foreach(var r in touched)for(int y=r.Top;y<r.Bottom;y++)clean.AsSpan(y*stride+r.X*4,r.Width*4).CopyTo(work.AsSpan(y*stride+r.X*4));
            DrawCursor(cursor);
            if(Diff(sequence+1,first))sequence++;
            return sequence;
        }
    }
    // True when the desktop image may have changed; `touched` receives the scaled areas it may affect.
    private bool Grab()
    {
        touched.Clear();long now=Environment.TickCount64;
        if(duplication is null&&now>=retryDuplication){
            duplication=DesktopDuplication.TryCreate(bounds);duplicated=false;
            if(duplication is null){retryDuplication=now+retryDelay;retryDelay=Math.Min(retryDelay*2,60_000);}else retryDelay=3000;
        }
        if(duplication is not null){
            try{
                if(duplication.TryMap(out var data,out int pitch,areas)){
                    try{foreach(var area in areas){var scaled=Resample.Affected(area,bounds.Width,bounds.Height,width,height);Resample.Bilinear(data,pitch,bounds.Width,bounds.Height,(byte*)Address(clean),width*4,width,height,scaled);touched.Add(scaled);}}
                    finally{duplication.Unmap();}
                    duplicated=true;return true; // the first image is always copied whole
                }
                if(duplicated)return false;
            }catch(IOException){duplication.Dispose();duplication=null;retryDuplication=now+500;}
        }
        grab??=new Gdi.Dib(bounds.Width,bounds.Height);grab.CopyFromScreen(bounds);
        Resample.Bilinear((byte*)grab.Bits,grab.Stride,bounds.Width,bounds.Height,(byte*)Address(clean),width*4,width,height);
        touched.Add(new(0,0,width,height));
        // Duplication already existed, so it reports every change after this grab: one grab is enough.
        duplicated=duplication is not null;return true;
    }
    private Rectangle CursorBox(Point cursor)
    {
        if(!bounds.Contains(cursor))return Rectangle.Empty;
        double sx=(double)width/bounds.Width,sy=(double)height/bounds.Height;
        var box=new Rectangle((int)((cursor.X-bounds.X)*sx),(int)((cursor.Y-bounds.Y)*sy),Math.Max(1,(int)(Cursors.Default.Size.Width*sx)),Math.Max(1,(int)(Cursors.Default.Size.Height*sy)));
        return Rectangle.Intersect(box,new(0,0,width,height));
    }
    // Blends a cached arrow into `work`; drawing through GDI+ every frame cost ~5 ms.
    private void DrawCursor(Point cursor)
    {
        var box=CursorBox(cursor);if(box.IsEmpty)return;
        double sx=(double)width/bounds.Width,sy=(double)height/bounds.Height;
        int w=Math.Max(1,(int)(Cursors.Default.Size.Width*sx)),h=Math.Max(1,(int)(Cursors.Default.Size.Height*sy));
        var patch=cursorPatch??=CursorPatch(w,h);uint* image=(uint*)Address(work);
        for(int y=0;y<box.Height;y++)for(int x=0;x<box.Width;x++){
            uint p=patch[y*w+x],a=p>>24;if(a==0)continue;
            uint* d=image+(box.Y+y)*width+box.X+x;
            *d=Resample.Mix(*d,p,a+(a>>7));
        }
    }
    // Renders the arrow on black and on white; the difference gives each pixel's coverage.
    private static uint[] CursorPatch(int w,int h)
    {
        using var black=new Bitmap(w,h,PixelFormat.Format32bppRgb);using var white=new Bitmap(w,h,PixelFormat.Format32bppRgb);
        foreach(var (image,background) in new[]{(black,Color.Black),(white,Color.White)}){using var g=Graphics.FromImage(image);g.Clear(background);Cursors.Default.DrawStretched(g,new Rectangle(0,0,w,h));}
        var patch=new uint[w*h];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){
            Color b=black.GetPixel(x,y),c=white.GetPixel(x,y);int a=255-Math.Max(c.R-b.R,Math.Max(c.G-b.G,c.B-b.B));
            if(a>0)patch[y*w+x]=(uint)a<<24|(uint)(b.R*255/a)<<16|(uint)(b.G*255/a)<<8|(uint)(b.B*255/a);
        }
        return patch;
    }
    // Copies touched tiles that differ from what viewers hold into `current` and stamps their version.
    private bool Diff(long version,bool all)
    {
        Array.Clear(touchedTiles);
        foreach(var r in touched)if(!r.IsEmpty)for(int row=r.Top/Tile;row<=(r.Bottom-1)/Tile;row++)for(int c=r.Left/Tile;c<=(r.Right-1)/Tile;c++)touchedTiles[row*columns+c]=true;
        bool changed=false;int stride=width*4;
        for(int i=0;i<touchedTiles.Length;i++){
            if(!touchedTiles[i])continue;
            int x=i%columns*Tile*4,y=i/columns*Tile,bytes=Math.Min(Tile*4,stride-x),end=Math.Min(height,y+Tile),r=y;
            if(!all)while(r<end&&work.AsSpan(r*stride+x,bytes).SequenceEqual(current.AsSpan(r*stride+x,bytes)))r++;
            if(r==end)continue;
            for(r=y;r<end;r++)work.AsSpan(r*stride+x,bytes).CopyTo(current.AsSpan(r*stride+x));
            versions[i]=version;changed=true;
        }
        return changed;
    }
    public TileUpdate Changes(long since)
    {
        lock(gate){
            if(sequence==start)Capture();
            var tiles=new List<ushort>();for(int i=0;i<versions.Length;i++)if(versions[i]>since)tiles.Add((ushort)i);
            var update=new TileUpdate(width,height,Tile,tiles.ToArray(),[],sequence);if(tiles.Count==0)return update;
            var (atlasColumns,atlasRows)=update.Atlas;int atlasStride=atlasColumns*Tile*4,stride=width*4;
            for(int i=0;i<tiles.Count;i++){
                var s=update.Slot(i);int to=s.AtlasY*atlasStride+s.AtlasX*4;
                for(int r=0;r<s.Height;r++)current.AsSpan((s.Y+r)*stride+s.X*4,s.Width*4).CopyTo(atlas.AsSpan(to+r*atlasStride));
            }
            return update with{Jpeg=Encode(atlas,atlasColumns*Tile,atlasRows*Tile,atlasStride)};
        }
    }
    public ScreenFrame Frame()
    {
        lock(gate){
            if(sequence==start)Capture();
            if(frameSequence!=sequence){frame=new(width,height,Encode(current,width,height,width*4));frameSequence=sequence;}
            return frame!;
        }
    }
    private byte[] Encode(byte[] pixels,int w,int h,int stride){using var image=new Bitmap(w,h,stride,PixelFormat.Format32bppRgb,Address(pixels));return EncodeJpeg(image,quality);}
    internal static byte[] EncodeJpeg(Bitmap image,long quality)
    {
        using var parameters=new EncoderParameters(1);parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality);
        using var stream=new MemoryStream();image.Save(stream,Jpeg,parameters);return stream.ToArray();
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;duplication?.Dispose();grab?.Dispose();}}
}
