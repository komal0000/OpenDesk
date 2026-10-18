using System.Text;

namespace OpenDesk.Core;

public sealed class MjpegRecording : IDisposable
{
    private readonly FileStream stream;
    private readonly BinaryWriter writer;
    private readonly List<(uint Offset,uint Length)> index=[];
    private readonly int width,height;
    private readonly long frameCount,streamCount,movi,microseconds,rate;
    private readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
    private bool disposed;
    public MjpegRecording(string path,int width,int height)
    {
        ScreenFrame.Validate(width,height,4);this.width=width;this.height=height;
        stream=new(path,FileMode.Create,FileAccess.ReadWrite,FileShare.Read);writer=new(stream,Encoding.ASCII,true);
        Four("RIFF");writer.Write(0);Four("AVI ");long hdrl=List("hdrl");
        Four("avih");writer.Write(56);microseconds=stream.Position;writer.Write(100_000);writer.Write(0);writer.Write(0);writer.Write(0x10);frameCount=stream.Position;writer.Write(0);writer.Write(0);writer.Write(1);writer.Write(width*height*3);writer.Write(width);writer.Write(height);writer.Write(new byte[16]);
        long strl=List("strl");Four("strh");writer.Write(56);Four("vids");Four("MJPG");writer.Write(0);writer.Write((ushort)0);writer.Write((ushort)0);writer.Write(0);writer.Write(1000);rate=stream.Position;writer.Write(10000);writer.Write(0);streamCount=stream.Position;writer.Write(0);writer.Write(width*height*3);writer.Write(-1);writer.Write(0);writer.Write((short)0);writer.Write((short)0);writer.Write((short)width);writer.Write((short)height);
        Four("strf");writer.Write(40);writer.Write(40);writer.Write(width);writer.Write(height);writer.Write((ushort)1);writer.Write((ushort)24);Four("MJPG");writer.Write(width*height*3);writer.Write(new byte[16]);EndList(strl);EndList(hdrl);movi=List("movi");
    }
    private void Four(string s)=>writer.Write(Encoding.ASCII.GetBytes(s));
    private long List(string type){Four("LIST");long size=stream.Position;writer.Write(0);Four(type);return size;}
    private void At(long pos,int value){long end=stream.Position;stream.Position=pos;writer.Write(value);stream.Position=end;}
    private void EndList(long size)=>At(size,checked((int)(stream.Position-size-4)));
    public void Add(ScreenFrame frame)
    {
        if(disposed)throw new ObjectDisposedException(nameof(MjpegRecording));
        if(frame.Width!=width||frame.Height!=height)throw new InvalidOperationException("Display size changed. Start a new recording.");
        if(stream.Length>1_500_000_000)throw new IOException("Recording reached its 1.5 GB limit.");
        uint offset=checked((uint)(stream.Position-(movi+4)));Four("00dc");writer.Write(frame.Jpeg.Length);writer.Write(frame.Jpeg);if((frame.Jpeg.Length&1)!=0)writer.Write((byte)0);index.Add((offset,(uint)frame.Jpeg.Length));
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        try{EndList(movi);Four("idx1");writer.Write(index.Count*16);foreach(var entry in index){Four("00dc");writer.Write(0x10);writer.Write(entry.Offset);writer.Write(entry.Length);}At(frameCount,index.Count);At(streamCount,index.Count);if(index.Count>0){double fps=Math.Clamp(index.Count/Math.Max(.1,clock.Elapsed.TotalSeconds),.01,1000);At(microseconds,(int)(1_000_000/fps));At(rate,Math.Max(1,(int)(fps*1000)));}At(4,checked((int)stream.Length-8));writer.Flush();}
        finally{writer.Dispose();stream.Dispose();}
    }
}
