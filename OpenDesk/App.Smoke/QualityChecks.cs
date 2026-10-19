using System.Diagnostics;
using System.Text.Json;
using OpenDesk.App;

namespace OpenDesk.Smoke;

internal static class QualityChecks
{
    internal static async Task RunAsync()
    {
        var results=new List<string>();
        string folder=Path.Combine(AppContext.BaseDirectory,"quality");Directory.CreateDirectory(folder);
        var timings=new List<double>();int bytes=0;
        using var animation=new Form{Text="OpenDesk capture benchmark",ClientSize=new(320,180),StartPosition=FormStartPosition.Manual,Location=new(30,30),TopMost=true,ShowInTaskbar=false};animation.Show();
        using(var capture=new CaptureScope(Screen.PrimaryScreen!.Bounds)){
            for(int i=0;i<35;i++) { animation.BackColor=Color.FromArgb(i*7%255,i*13%255,i*19%255);animation.Refresh();await Task.Delay(20);var watch=Stopwatch.StartNew();capture.Source.Capture();var frame=capture.Source.Frame();watch.Stop();if(i>4)timings.Add(watch.Elapsed.TotalMilliseconds);bytes=frame.Jpeg.Length; }
        }
        animation.Close();
        timings.Sort();results.Add($"Capture+encode p50={timings[timings.Count/2]:F1} ms p95={timings[(int)(timings.Count*.95)]:F1} ms JPEG={bytes} bytes");
        int failures=0;
        foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(var size in new[]{new Size(800,600),new Size(1024,640),new Size(1280,800)}){
            using var form=new MainForm(false,new ClientSettings());form.ShowInTaskbar=false;form.Opacity=0;form.MinimumSize=Size.Empty;form.Show();
            if(scale!=1){var fonts=Descendants(form).Prepend(form).Select(c=>(Control:c,Font:c.Font)).ToArray();form.Scale(new SizeF(scale,scale));foreach(var entry in fonts)entry.Control.Font=new Font(entry.Font.FontFamily,entry.Font.Size*scale,entry.Font.Style);}
            form.ClientSize=new((int)(size.Width*scale),(int)(size.Height*scale));form.PerformLayout();await Task.Delay(30);
            var buttons=Descendants(form).OfType<Button>().Where(b=>b.Visible&&b.Text.Length>0).ToArray();
            foreach(var button in buttons){
                int width=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+button.Padding.Horizontal+8;
                if(button.ClientSize.Width+2<width){results.Add($"CLIPPED {size} scale={scale} '{button.Text}' width={button.ClientSize.Width} needs={width}");failures++;}
                if(button.Parent is FlowLayoutPanel {Dock:DockStyle.Left} sidebar&&button.Right>sidebar.ClientSize.Width-sidebar.Padding.Right){results.Add($"SIDEBAR CLIPPED {size} scale={scale} '{button.Text}' right={button.Right} available={sidebar.ClientSize.Width-sidebar.Padding.Right}");failures++;}
            }
            var label=Descendants(form).OfType<Label>().FirstOrDefault(c=>c.Text=="Remote address");
            if(label is not null&&label.Height<TextRenderer.MeasureText(label.Text,label.Font,new Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak).Height){results.Add($"CLIPPED address label {size} scale={scale}");failures++;}
            foreach(var table in Descendants(form).OfType<ScrollableControl>().Where(p=>p.Name=="ContentStack"&&p.HorizontalScroll.Visible)){results.Add($"UNWANTED HORIZONTAL SCROLL {size} scale={scale} client={table.ClientSize} display={table.DisplayRectangle}");failures++;}
            using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(folder,$"home-{size.Width}-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));form.Close();
        }
        foreach(var page in new[]{"incoming","recent","favorites","contacts"}){
            using var home=new MainForm(false,new ClientSettings()){Opacity=0,ShowInTaskbar=false};home.Show();home.ClientSize=new(800,600);
            ((Button)home.Controls.Find("Nav-"+page,true).Single()).PerformClick();await Task.Delay(20);
            foreach(var button in Descendants(home).OfType<Button>())if(button.Visible&&button.Width<TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+button.Padding.Horizontal){results.Add($"PAGE BUTTON CLIPPED {page}: {button.Text}");failures++;}
            home.Close();
        }
        foreach(float scale in new[]{1f,1.25f,1.5f,2f})foreach(Form dialog in new Form[]{new ApprovalForm("Expected support person"),new ServiceSettingsForm(new ClientSettings()),new ChatForm("Test device",_=>Task.CompletedTask)}){
            using(dialog){dialog.Opacity=0;dialog.ShowInTaskbar=false;dialog.Show();
                if(scale!=1){var fonts=Descendants(dialog).Prepend(dialog).Select(c=>(Control:c,Font:c.Font)).ToArray();dialog.Scale(new SizeF(scale,scale));foreach(var entry in fonts)entry.Control.Font=new Font(entry.Font.FontFamily,entry.Font.Size*scale,entry.Font.Style);}
                dialog.PerformLayout();await Task.Delay(20);
                foreach(var button in Descendants(dialog).OfType<Button>()){
                    int needed=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+button.Padding.Horizontal;
                    if(button.Width<needed){results.Add($"DIALOG BUTTON CLIPPED {dialog.Text} scale={scale}: {button.Text}");failures++;}
                    if(dialog is ApprovalForm&& !dialog.ClientRectangle.Contains(dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle)))){results.Add($"CONSENT BUTTON OUTSIDE VIEW {scale}: {button.Text}");failures++;}
                }
                using var image=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(image,new Rectangle(Point.Empty,dialog.Size));image.Save(Path.Combine(folder,$"{dialog.GetType().Name}-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));dialog.Close();
            }
        }
        using(var ordinary=new Form())using(var button=new Button())using(var approval=new ApprovalForm("Test")){
            ordinary.Controls.Add(button);if(RemoteInputFilter.IsProtected(button))throw new Exception("Ordinary controls should accept approved input");
            button.Tag="LocalPermission";if(!RemoteInputFilter.IsProtected(button)||!RemoteInputFilter.IsProtected(approval))throw new Exception("Consent/permission input guard missing");
        }
        results.Add($"Layout failures: {failures}");File.WriteAllLines(Path.Combine(folder,"results.txt"),results);
        if(failures>0)throw new Exception($"{failures} clipped controls; see quality/results.txt");
    }
    internal static IEnumerable<Control> Descendants(Control root){foreach(Control control in root.Controls){yield return control;foreach(var child in Descendants(control))yield return child;}}
    private sealed class CaptureScope(Rectangle bounds):IDisposable { internal ScreenCapture Source {get;}=new(bounds); public void Dispose(){if((object)Source is IDisposable source)source.Dispose();} }
}
