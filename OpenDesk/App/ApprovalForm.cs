using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class ApprovalForm : Form
{
    private readonly TaskCompletionSource<Approval> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<Approval> Answer => answer.Task;
    internal ApprovalForm(string name)
    {
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);
        Text = "OpenDesk · Connection request"; ClientSize = new(600, 390); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; BackColor = Color.White;
        var panel = Theme.Stack();
        panel.Controls.Add(Theme.Label("Someone wants to view your screen", 17, true));
        var detail = Theme.Label($"Display name: {name}\nThis name is self-reported. Approve only someone you expect.");
        detail.MaximumSize = new(470, 0); panel.Controls.Add(detail);
        var clipboard=new CheckBox{Text="Allow clipboard text sharing",AutoSize=true};var files=new CheckBox{Text="Allow file transfers (each received file needs a save location)",AutoSize=true};panel.Controls.Add(clipboard);panel.Controls.Add(files);
        var reject = Theme.Button("Decline"); var watch = Theme.Button("Allow viewing", true); var control = Theme.Button("Allow control");
        reject.Click += (_, _) => Finish(new(false)); watch.Click += (_, _) => Finish(new(true,false,clipboard.Checked,files.Checked)); control.Click += (_, _) => Finish(new(true,true,clipboard.Checked,files.Checked));
        panel.Controls.Add(Theme.Row(reject, watch, control)); Controls.Add(panel);
        FormClosed += (_, _) => answer.TrySetResult(new(false));
    }
    private void Finish(Approval value) { answer.TrySetResult(value); Close(); }
}
