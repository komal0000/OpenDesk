using System.Runtime.InteropServices;

namespace OpenDesk.App;

// DXGI Desktop Duplication: the compositor hands over each new desktop image on the GPU, so a
// capture costs one texture copy instead of a 30 ms GDI read-back, and idle screens cost nothing.
// COM calls go through vtable slots to avoid an interop dependency.
internal sealed unsafe class DesktopDuplication : IDisposable
{
    private const int WaitTimeout=unchecked((int)0x887A0027),NotFound=unchecked((int)0x887A0002);
    private static readonly Guid Factory1=new("770aae78-f26f-4dba-a829-253c83d1b387"),Output1=new("00cddea8-939b-4b83-a340-a685226666cc"),Texture2D=new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(in Guid iid,out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,int driverType,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out int level,out nint context);
    [StructLayout(LayoutKind.Sequential)] private struct OutputDesc{public fixed char Name[32];public Rectangle32 Desktop;public int Attached,Rotation;public nint Monitor;}
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle32{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct DuplicationDesc{public uint Width,Height,RefreshNumerator,RefreshDenominator,Format,Scanline,Scaling;public int Rotation,InSystemMemory;}
    [StructLayout(LayoutKind.Sequential)] private struct FrameInfo{public long LastPresent,LastMouse;public uint Accumulated;public int Coalesced,Masked,PointerX,PointerY,PointerVisible;public uint Metadata,PointerShape;}
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc{public uint Width,Height,Mips,Array,Format,SampleCount,SampleQuality,Usage,Bind,CpuAccess,Misc;}
    [StructLayout(LayoutKind.Sequential)] private struct Mapped{public byte* Data;public uint RowPitch,DepthPitch;}

    private nint device,context,duplication,staging;
    private bool holding,mapped;
    private byte[] metadata=[];
    internal int Width{get;}
    internal int Height{get;}
    private DesktopDuplication(nint device,nint context,nint duplication,int width,int height){this.device=device;this.context=context;this.duplication=duplication;Width=width;Height=height;}

    private static nint Slot(nint com,int index)=>(*(nint**)com)[index];
    private static void Release(ref nint com){if(com!=0){((delegate* unmanaged[Stdcall]<nint,uint>)Slot(com,2))(com);com=0;}}
    private static int Query(nint com,Guid iid,out nint result){nint value;int hr=((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Slot(com,0))(com,&iid,&value);result=value;return hr;}

    // Null when this monitor cannot be duplicated (remote session, rotated or secure desktop, old driver).
    internal static DesktopDuplication? TryCreate(Rectangle bounds)
    {
        nint factory=0,adapter=0,output=0,output1=0,device=0,context=0,duplication=0;
        try{
            if(CreateDXGIFactory1(Factory1,out factory)<0)return null;
            for(uint a=0;;a++){
                Release(ref adapter);nint next;
                if(((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)Slot(factory,12))(factory,a,&next)<0)return null;adapter=next;
                for(uint o=0;;o++){
                    Release(ref output);nint found;
                    int hr=((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)Slot(adapter,7))(adapter,o,&found);
                    if(hr==NotFound)break;if(hr<0)return null;output=found;
                    OutputDesc desc;if(((delegate* unmanaged[Stdcall]<nint,OutputDesc*,int>)Slot(output,7))(output,&desc)<0)continue;
                    if(desc.Desktop.Left!=bounds.Left||desc.Desktop.Top!=bounds.Top||desc.Desktop.Right!=bounds.Right||desc.Desktop.Bottom!=bounds.Bottom)continue;
                    // The device must live on the adapter that drives this output (hybrid laptops).
                    if(D3D11CreateDevice(adapter,0,0,0x20,0,0,7,out device,out _,out context)<0||Query(output,Output1,out output1)<0)return null;
                    if(((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)Slot(output1,22))(output1,device,&duplication)<0)return null;
                    DuplicationDesc info;((delegate* unmanaged[Stdcall]<nint,DuplicationDesc*,void>)Slot(duplication,7))(duplication,&info);
                    if(info.Rotation>1||info.Width!=bounds.Width||info.Height!=bounds.Height)return null;
                    var result=new DesktopDuplication(device,context,duplication,bounds.Width,bounds.Height);device=context=duplication=0;return result;
                }
            }
        }
        catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException){return null;}
        finally{Release(ref duplication);Release(ref context);Release(ref device);Release(ref output1);Release(ref output);Release(ref adapter);Release(ref factory);}
    }

    // Maps the newest desktop image when the screen changed since the last call. Unmap afterwards.
    // The first image is copied whole; later ones copy only their changed areas into the mirror.
    // Throws when duplication was lost (mode change, desktop switch); recreate it later.
    // `changed` receives the desktop areas that differ from the previous mapped image.
    internal bool TryMap(out byte* data,out int pitch,List<Rectangle> changed)
    {
        data=null;pitch=0;Unmap();changed.Clear();ReleaseFrame();
        FrameInfo info;nint resource;
        int hr=((delegate* unmanaged[Stdcall]<nint,uint,FrameInfo*,nint*,int>)Slot(duplication,8))(duplication,0,&info,&resource);
        if(hr==WaitTimeout)return false;
        if(hr<0)throw new IOException($"Desktop duplication stopped (0x{hr:X8}).");
        holding=true;
        try{
            // No present: pointer-only update, or the first frame, whose texture holds no image yet.
            if(info.LastPresent==0)return false;
            if(Query(resource,Texture2D,out var texture)<0)throw new IOException("Desktop image unavailable.");
            try{
                if(staging==0){
                    var desc=new TextureDesc{Width=(uint)Width,Height=(uint)Height,Mips=1,Array=1,Format=87,SampleCount=1,Usage=3,CpuAccess=0x20000};nint created;
                    if(((delegate* unmanaged[Stdcall]<nint,TextureDesc*,nint,nint*,int>)Slot(device,5))(device,&desc,0,&created)<0)throw new IOException("Could not allocate a capture texture.");staging=created;
                }
                else ChangedAreas(info.Metadata,changed);
                // The staging texture mirrors the desktop, so only changed areas need copying from the GPU.
                if(changed.Count is 0 or >32){changed.Clear();changed.Add(new(0,0,Width,Height));((delegate* unmanaged[Stdcall]<nint,nint,nint,void>)Slot(context,47))(context,staging,texture);}
                else{uint* box=stackalloc uint[6];foreach(var r in changed){
                    box[0]=(uint)r.Left;box[1]=(uint)r.Top;box[2]=0;box[3]=(uint)r.Right;box[4]=(uint)r.Bottom;box[5]=1;
                    ((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,uint,nint,uint,uint*,void>)Slot(context,46))(context,staging,0,(uint)r.Left,(uint)r.Top,0,texture,0,box);
                }}
            }finally{Release(ref texture);}
            Mapped map;
            if(((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)Slot(context,14))(context,staging,0,1,0,&map)<0)throw new IOException("Could not read the desktop image.");
            mapped=true;data=map.Data;pitch=(int)map.RowPitch;return true;
        }finally{Release(ref resource);}
    }
    // Moved and dirty rectangles of the acquired frame, clipped to the output.
    private void ChangedAreas(uint size,List<Rectangle> changed)
    {
        if(size==0)return;
        if(metadata.Length<size)metadata=new byte[size];
        fixed(byte* buffer=metadata){
            uint used;
            if(((delegate* unmanaged[Stdcall]<nint,uint,byte*,uint*,int>)Slot(duplication,10))(duplication,size,buffer,&used)<0)return;
            for(uint i=0;i+24<=used;i+=24)Add(*(Rectangle32*)(buffer+i+8)); // POINT source, then RECT destination
            if(((delegate* unmanaged[Stdcall]<nint,uint,byte*,uint*,int>)Slot(duplication,9))(duplication,size,buffer,&used)<0){changed.Clear();return;}
            for(uint i=0;i+16<=used;i+=16)Add(*(Rectangle32*)(buffer+i));
        }
        void Add(Rectangle32 r){var area=Rectangle.Intersect(Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom),new(0,0,Width,Height));if(!area.IsEmpty)changed.Add(area);}
    }
    private void ReleaseFrame(){if(holding){((delegate* unmanaged[Stdcall]<nint,int>)Slot(duplication,14))(duplication);holding=false;}}
    internal void Unmap(){if(mapped){((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)Slot(context,15))(context,staging,0);mapped=false;}}
    public void Dispose(){
        Unmap();ReleaseFrame();
        Release(ref staging);Release(ref duplication);Release(ref context);Release(ref device);
    }
}
