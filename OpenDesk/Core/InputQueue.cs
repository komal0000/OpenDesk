using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OpenDesk.Core;

// Coalesce only adjacent motion. Buttons/keys retain their order and coordinates.
public sealed class InputQueue
{
    private readonly object gate=new();
    private readonly LinkedList<InputEvent> queue=new();
    private readonly Channel<byte> wake=Channel.CreateBounded<byte>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite});
    private bool complete;
    public bool Post(InputEvent input){lock(gate){
        if(complete)return false;
        if(input.Kind=="move"&&queue.Last?.Value.Kind=="move")queue.Last.Value=input;
        else{
            if(queue.Count>=128){var motion=queue.First;while(motion is not null&&motion.Value.Kind!="move")motion=motion.Next;if(motion is null)return false;queue.Remove(motion);}
            queue.AddLast(input);
        }
        wake.Writer.TryWrite(1);return true;
    }}
    public void Complete(){lock(gate){complete=true;wake.Writer.TryComplete();}}
    public async IAsyncEnumerable<InputEvent> ReadAllAsync([EnumeratorCancellation]CancellationToken ct){
        while(await wake.Reader.WaitToReadAsync(ct)){
            wake.Reader.TryRead(out _);
            while(true){InputEvent? input;lock(gate){input=queue.First?.Value;if(input is not null)queue.RemoveFirst();}if(input is null)break;yield return input;}
        }
    }
}
