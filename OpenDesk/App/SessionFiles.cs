using OpenDesk.Core;

namespace OpenDesk.App;

internal static class SessionFiles
{
    internal static Task<string?> ChooseSaveAsync(Form owner,FileOffer offer,CancellationToken ct)
    {
        var answer=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(owner.IsDisposed||!owner.IsHandleCreated)return Task.FromResult<string?>(null);
        owner.BeginInvoke(()=>{
            if(ct.IsCancellationRequested){answer.TrySetResult(null);return;}
            using var dialog=new SaveFileDialog{Title=$"Receive {offer.Name} ({offer.Length:N0} bytes)",FileName=offer.Name,OverwritePrompt=true,AddExtension=false};
            answer.TrySetResult(dialog.ShowDialog(owner)==DialogResult.OK&&!ct.IsCancellationRequested?dialog.FileName:null);
        });
        return answer.Task.WaitAsync(ct);
    }
    internal static string? PickSend(Form owner)
    {
        using var dialog=new OpenFileDialog{Title="Send a file (up to 100 MiB)",CheckFileExists=true,Multiselect=false};
        return dialog.ShowDialog(owner)==DialogResult.OK?dialog.FileName:null;
    }
}
