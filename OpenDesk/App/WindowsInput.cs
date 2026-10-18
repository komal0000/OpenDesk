using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class WindowsInput(Func<Rectangle> selectedMonitor) : IInputSink
{
    internal WindowsInput(Rectangle monitor):this(()=>monitor){}
    private readonly HashSet<int> keys = [];
    private readonly HashSet<int> buttons = [];
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    private static void Send(params Input[] values) {
        for(int i=0;i<values.Length;i++){
            if(values[i].Type==1)values[i].Data.Keyboard.Extra=RemoteInputFilter.Marker;
            else values[i].Data.Mouse.Extra=RemoteInputFilter.Marker;
        }
        if (SendInput((uint)values.Length, values, Marshal.SizeOf<Input>()) != values.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked remote input. Elevated and secure desktops are not supported.");
    }
    private static uint ButtonFlag(int button, bool down) => (button, down) switch {
        (1,true) => 0x2, (1,false) => 0x4, (2,true) => 0x8, (2,false) => 0x10, (3,true) => 0x20, (3,false) => 0x40, _ => 0
    };
    private static bool Extended(int key) => key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2d or 0x2e or 0x6f or 0x90 or 0xa3 or 0xa5 or 0x5b or 0x5c;
    public void Apply(InputEvent input)
    {
        if (!input.IsValid) return;
        Input? pointer=null;
        if (input.Kind is "move" or "button" or "wheel") {
            var monitor=selectedMonitor();
            var p = ViewerCoordinates.DesktopPoint(input.X, input.Y, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            var desktop = SystemInformation.VirtualScreen;
            pointer=new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput {
                X = (int)Math.Round((p.X - desktop.Left) * 65535.0 / Math.Max(1, desktop.Width - 1)),
                Y = (int)Math.Round((p.Y - desktop.Top) * 65535.0 / Math.Max(1, desktop.Height - 1)), Flags = 0x8000 | 0x4000 | 0x1
            } } };
        }
        if (input.Kind == "key") {
            Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = (ushort)input.Code, Flags = (input.Down ? 0u : 2u) | (Extended(input.Code) ? 1u : 0u) } } });
            if (input.Down) keys.Add(input.Code); else keys.Remove(input.Code);
        }
        else if (input.Kind == "button") {
            Send(pointer!.Value,new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = ButtonFlag(input.Code, input.Down) } } });
            if (input.Down) buttons.Add(input.Code); else buttons.Remove(input.Code);
        }
        else if (input.Kind == "wheel") Send(pointer!.Value,new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 0x800, Data = unchecked((uint)input.Delta) } } });
        else if(pointer.HasValue)Send(pointer.Value);
    }
    public void ReleaseAll()
    {
        foreach (int key in keys.ToArray()) {
            try { Send(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = (ushort)key, Flags = 2u | (Extended(key) ? 1u : 0u) } } }); }
            catch (Win32Exception) { }
        }
        foreach (int button in buttons.ToArray()) {
            try { Send(new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = ButtonFlag(button, false) } } }); }
            catch (Win32Exception) { }
        }
        keys.Clear(); buttons.Clear();
    }
}
