using System.Security.Cryptography;

namespace OpenDesk.Core;

public sealed record FileOffer(Guid Id,string Name,long Length,string Sha256);
public sealed record FileAck(Guid Id,bool Accepted,string Stage);

public sealed class FileTransfer(Func<Message,CancellationToken,Task> send,Func<FileOffer,CancellationToken,Task<string?>> chooseSave) : IAsyncDisposable
{
    public const long MaxBytes=100*1024*1024;
    private FileStream? incoming;
    private IncrementalHash? hash;
    private FileOffer? offer;
    private string? temporary,target;
    private long received;
    private readonly object gate=new();
    private Guid sending;
    private TaskCompletionSource<FileAck>? ready,done;
    public event Action<string>? Notice;
    public async Task SendAsync(string path,CancellationToken ct)
    {
        await using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,16*1024,true);
        if(!source.CanSeek||source.Length>MaxBytes)throw new IOException("Choose a regular file up to 100 MiB.");
        var id=Guid.NewGuid();Task<FileAck> waitReady,waitDone;
        lock(gate){if(sending!=Guid.Empty)throw new InvalidOperationException("A file is already being sent.");sending=id;ready=new(TaskCreationOptions.RunContinuationsAsynchronously);done=new(TaskCreationOptions.RunContinuationsAsynchronously);waitReady=ready.Task;waitDone=done.Task;}
        bool completed=false;
        try {
            string digest=Convert.ToHexString(await SHA256.HashDataAsync(source,ct));source.Position=0;
            await send(Wire.Json(MessageKind.FileOffer,new FileOffer(id,Path.GetFileName(path),source.Length,digest)),ct);
            var answer=await waitReady.WaitAsync(TimeSpan.FromMinutes(2),ct);if(!answer.Accepted)throw new IOException("The receiver declined the file.");
            byte[] buffer=new byte[16*1024-16];int length;long sent=0,progress=Environment.TickCount64;
            while((length=await source.ReadAsync(buffer,ct))>0){byte[] chunk=new byte[16+length];id.TryWriteBytes(chunk);buffer.AsSpan(0,length).CopyTo(chunk.AsSpan(16));await send(new(MessageKind.FileChunk,chunk),ct);sent+=length;if(Environment.TickCount64-progress>=250){progress=Environment.TickCount64;Notice?.Invoke($"Sending {Path.GetFileName(path)} · {sent*100/Math.Max(1,source.Length)}%");}}
            await send(Wire.Json(MessageKind.FileFinish,id),ct);
            var complete=await waitDone.WaitAsync(TimeSpan.FromMinutes(2),ct);if(!complete.Accepted)throw new IOException("File verification failed.");
            Notice?.Invoke("Sent " + Path.GetFileName(path));
            completed=true;
        } finally {if(!completed){try{await send(Wire.Json(MessageKind.FileAck,new FileAck(id,false,"cancel")),CancellationToken.None);}catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException){}}lock(gate){sending=Guid.Empty;ready=null;done=null;}}
    }
    public async Task ReceiveAsync(Message message,CancellationToken ct)
    {
        if(message.Kind==MessageKind.FileAck){var ack=Wire.Decode<FileAck>(message);if(ack.Stage=="cancel"&&offer?.Id==ack.Id){if(incoming is not null)await incoming.DisposeAsync();incoming=null;hash?.Dispose();hash=null;CleanupPartial();offer=null;}lock(gate){if(ack.Id==sending){if(ack.Stage=="ready")ready?.TrySetResult(ack);else if(ack.Stage=="done")done?.TrySetResult(ack);}}return;}
        if(message.Kind==MessageKind.FileOffer){
            var request=Wire.Decode<FileOffer>(message);
            if(offer is not null || request.Id==Guid.Empty || request.Length is <0 or >MaxBytes || !Invitation.IsHex(request.Sha256,64) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length>128 || request.Name.IndexOfAny(['/', '\\', ':'])>=0 || request.Name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidDataException("Invalid file offer.");
            string? chosen=await chooseSave(request,ct).WaitAsync(ct);
            if(chosen is null){await send(Wire.Json(MessageKind.FileAck,new FileAck(request.Id,false,"ready")),ct);return;}
            target=Path.GetFullPath(chosen);temporary=target+"."+request.Id.ToString("N")+".opendesk-part";
            incoming=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,16*1024,true);hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);offer=request;received=0;
            await send(Wire.Json(MessageKind.FileAck,new FileAck(request.Id,true,"ready")),ct);return;
        }
        if(message.Kind==MessageKind.FileChunk){
            if(offer is null||incoming is null||message.Payload.Length<17||new Guid(message.Payload.AsSpan(0,16))!=offer.Id||received+message.Payload.Length-16>offer.Length)throw new InvalidDataException("Unexpected file data.");
            await incoming.WriteAsync(message.Payload.AsMemory(16),ct);hash!.AppendData(message.Payload.AsSpan(16));received+=message.Payload.Length-16;return;
        }
        if(message.Kind==MessageKind.FileFinish){
            Guid id=Wire.Decode<Guid>(message);if(offer is null||incoming is null||id!=offer.Id)throw new InvalidDataException("Unexpected file completion.");
            bool valid=received==offer.Length&&CryptographicOperations.FixedTimeEquals(hash!.GetHashAndReset(),Convert.FromHexString(offer.Sha256));
            await incoming.DisposeAsync();incoming=null;hash?.Dispose();hash=null;
            if(valid){File.Move(temporary!,target!,true);temporary=null;Notice?.Invoke("Received " + offer.Name);}else CleanupPartial();
            offer=null;await send(Wire.Json(MessageKind.FileAck,new FileAck(id,valid,"done")),ct);
        }
    }
    private void CleanupPartial(){if(temporary is not null&&File.Exists(temporary)){try{File.Delete(temporary);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Notice?.Invoke("An incomplete file could not be removed; check the selected destination.");}}temporary=null;}
    public async ValueTask DisposeAsync(){if(incoming is not null)await incoming.DisposeAsync();incoming=null;hash?.Dispose();hash=null;CleanupPartial();offer=null;lock(gate){ready?.TrySetCanceled();done?.TrySetCanceled();}}
}
