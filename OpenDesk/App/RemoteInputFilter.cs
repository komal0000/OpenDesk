using System.Runtime.InteropServices;

namespace OpenDesk.App;

// Do not let our own injected input click approval or permission controls.
// This is an application UI guard, not a sandbox for an authorized desktop controller.
internal sealed class RemoteInputFilter : IMessageFilter
{
    internal const uint Marker = 0x4F444553;
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    internal static bool Blocks(int message, nuint extra) => extra == Marker &&
        (message is >= 0x100 and <= 0x109 or >= 0x200 and <= 0x20e);
    internal static bool IsProtected(Control? target){
        if(target?.FindForm() is ApprovalForm or ServiceSettingsForm or ViewerForm)return true;
        for(var current=target;current is not null;current=current.Parent)if(current.Tag is string tag&&tag=="LocalPermission")return true;
        return false;
    }
    public bool PreFilterMessage(ref System.Windows.Forms.Message m) => Blocks(m.Msg, unchecked((nuint)GetMessageExtraInfo()))&&IsProtected(Control.FromChildHandle(m.HWnd));
}
