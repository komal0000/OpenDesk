using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class ApprovalForm : Form
{
    private readonly TaskCompletionSource<Approval> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<Approval> Answer => answer.Task;
    internal ApprovalForm(string name)
    {
        SuspendLayout();
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);
        Text = "OpenDesk · Connection request"; ClientSize = new(600, 480); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; BackColor = Color.White;
        var panel = Theme.Stack();
        panel.Controls.Add(Theme.Label("Incoming connection",20,true));
        panel.Controls.Add(Theme.Description("Someone is requesting access to this computer."));
        var identity=Theme.Card();identity.Controls.Add(Theme.Label(name,14,true));identity.Controls.Add(Theme.Description("This name is self-reported. Approve only someone you expect."));panel.Controls.Add(identity);
        panel.Controls.Add(Theme.Label("Additional permissions",11,true));
        var clipboard=new CheckBox{Text="Allow clipboard text sharing",AccessibleName="Allow clipboard text sharing",AutoSize=true,Margin=new(0,8,0,8)};
        var files=new CheckBox{Text="Allow file transfers",AccessibleName="Allow file transfers",AutoSize=true,Margin=new(0,8,0,8)};
        panel.Controls.Add(clipboard);panel.Controls.Add(files);panel.Controls.Add(Theme.Description("Each received file needs a save location. These permissions start off."));
        var reject = Theme.Button("Decline"); var watch = Theme.Button("Allow viewing", true); var control = Theme.Button("Allow control");
        reject.Click += (_, _) => Finish(new(false)); watch.Click += (_, _) => Finish(new(true,false,clipboard.Checked,files.Checked)); control.Click += (_, _) => Finish(new(true,true,clipboard.Checked,files.Checked));
        Theme.Destructive(reject);CancelButton=reject;
        panel.Controls.Add(Theme.Row(reject, watch, control)); Controls.Add(panel);
        FormClosed += (_, _) => answer.TrySetResult(new(false));
        ResumeLayout(true);
    }
    private void Finish(Approval value) { answer.TrySetResult(value); Close(); }
}
