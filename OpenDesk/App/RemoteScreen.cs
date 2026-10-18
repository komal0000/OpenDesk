namespace OpenDesk.App;

internal sealed class RemoteScreen : PictureBox
{
    internal RemoteScreen() { SetStyle(ControlStyles.Selectable, true); TabStop = true; }
    protected override bool IsInputKey(Keys keyData) => true;
}
