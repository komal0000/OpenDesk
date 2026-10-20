namespace OpenDesk.App;

// Minimal COM calls through vtable slots, so DXGI and Media Foundation need no interop dependency.
internal static unsafe class Com
{
    internal static nint Slot(nint com, int index) => (*(nint**)com)[index];
    internal static void Release(ref nint com) { if (com != 0) { ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(com, 2))(com); com = 0; } }
    internal static int Query(nint com, Guid iid, out nint result) { nint value; int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(com, 0))(com, &iid, &value); result = value; return hr; }
}
