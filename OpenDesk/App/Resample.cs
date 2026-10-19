namespace OpenDesk.App;

internal static unsafe class Resample
{
    // Bilinear resize of 32-bit pixels, sampling at pixel centres, for the whole target or only
    // `area` of it. Two channels are blended per 32-bit multiply (0x00FF00FF lanes), and row
    // blocks run in parallel.
    internal static void Bilinear(byte* source,int sourceStride,int sourceWidth,int sourceHeight,byte* target,int targetStride,int width,int height,Rectangle? area=null)
    {
        var a=Rectangle.Intersect(area??new(0,0,width,height),new(0,0,width,height));if(a.IsEmpty)return;
        if(sourceWidth==width&&sourceHeight==height){
            for(int y=a.Top;y<a.Bottom;y++)Buffer.MemoryCopy(source+(long)y*sourceStride+a.X*4,target+(long)y*targetStride+a.X*4,a.Width*4,a.Width*4);
            return;
        }
        var xs=Axis(sourceWidth,width,a.Left,a.Width);var ys=Axis(sourceHeight,height,a.Top,a.Height);
        nint src=(nint)source,dst=(nint)target;
        // Small areas (a cursor, a few tiles) are cheaper inline than through the thread pool.
        if(a.Height<=32)Rows(0);else Parallel.For(0,(a.Height+15)/16,Rows);
        void Rows(int block){
            for(int y=a.Top+block*16,end=a.Height<=32?a.Bottom:Math.Min(a.Bottom,y+16);y<end;y++){
                var (y0,wy)=ys[y-a.Top];int y1=Math.Min(y0+1,sourceHeight-1);
                uint* top=(uint*)(src+(nint)y0*sourceStride),bottom=(uint*)(src+(nint)y1*sourceStride),row=(uint*)(dst+(nint)y*targetStride);
                for(int x=a.Left;x<a.Right;x++){
                    var (x0,wx)=xs[x-a.Left];int x1=Math.Min(x0+1,sourceWidth-1);
                    row[x]=Mix(Mix(top[x0],top[x1],wx),Mix(bottom[x0],bottom[x1],wx),wy);
                }
            }
        }
    }
    // Target pixels whose samples read any pixel of `changed` in the source.
    internal static Rectangle Affected(Rectangle changed,int sourceWidth,int sourceHeight,int width,int height)
    {
        double sx=(double)width/sourceWidth,sy=(double)height/sourceHeight;
        return Rectangle.Intersect(Rectangle.FromLTRB((int)((changed.Left-1)*sx)-1,(int)((changed.Top-1)*sy)-1,(int)Math.Ceiling((changed.Right+1)*sx)+1,(int)Math.Ceiling((changed.Bottom+1)*sy)+1),new(0,0,width,height));
    }
    // Source index and blend weight for target positions first..first+count.
    private static (int Index,uint Weight)[] Axis(int source,int target,int first,int count){
        var axis=new (int,uint)[count];double step=(double)source/target;
        for(int i=0;i<count;i++){double f=Math.Clamp((first+i+.5)*step-.5,0,source-1);int index=(int)f;axis[i]=(index,(uint)Math.Round((f-index)*256));}
        return axis;
    }
    // Blend two 32-bit pixels; w is the weight of b out of 256.
    internal static uint Mix(uint a,uint b,uint w){
        uint v=256-w;
        return ((a&0xFF00FF)*v+(b&0xFF00FF)*w>>8&0xFF00FF)|(((a>>8)&0xFF00FF)*v+((b>>8)&0xFF00FF)*w&0xFF00FF00);
    }
}
