using OpenDesk.App;

namespace OpenDesk.Smoke;

internal static class UiChecks
{
    private static readonly string Folder=Path.Combine(AppContext.BaseDirectory,"ui-checks");
    private static int screenshots;
    internal static void Run()
    {
        Directory.CreateDirectory(Folder);
        try {
            var profile=new ClientSettings {DeviceId="123456789",DisplayName="Alex · Support",StorageFile=Path.Combine(Folder,"profile.json")};
            profile.Contacts.AddRange([
                new("234567891","Studio workstation",true,new(2026,10,19,10,0,0,TimeSpan.Zero)),
                new("345678912","Design team's very long workstation name for layout verification",false,new(2026,10,18,10,0,0,TimeSpan.Zero)),
                new("456789123","Office laptop",true,new(2026,10,17,10,0,0,TimeSpan.Zero))]);
            using var home=new MainForm(false,profile);Show(home);
            int firstCardHeight=Descendants(home).OfType<Theme.DeviceCard>().First().Height;
            CheckContrast(Theme.Ink,Color.White);CheckContrast(Theme.Muted,Theme.Paper);CheckContrast(Color.White,Theme.Accent);CheckContrast(Theme.Accent,Theme.Selection);
            var toggle=home.Controls.Find("ToggleSharing",true).OfType<Button>().SingleOrDefault();
            Require(toggle is not null,"Home must offer a collapsed Display & sharing section.");
            var sharing=home.Controls.Find("SharingOptions",true).Single();Require(!sharing.Visible,"Sharing controls should start collapsed.");
            var remote=home.Controls.Find("RemoteAddress",true).Single();remote.Select();Require(home.SelectNextControl(remote,true,true,true,false)&&home.ActiveControl==home.Controls.Find("Connect",true).Single(),"Tab from the remote address must reach Connect.");
            Capture(home,"home");
            var recent=home.Controls.Find("DeviceCards",true).Single();Require(recent.Bottom<=recent.Parent!.ClientSize.Height,"Recent devices should appear above the fold at the default size.");
            toggle!.PerformClick();Application.DoEvents();Require(sharing.Visible,"Sharing controls should expand.");Capture(home,"sharing-expanded");
            var quality=Descendants(sharing).OfType<ComboBox>().Last();quality.SelectedIndex=2;
            Navigate(home,"incoming");Capture(home,"incoming");Navigate(home,"home");
            Require(Descendants(home).OfType<Theme.DeviceCard>().First().Height==firstCardHeight,"Device cards must keep their DPI size after navigation.");
            Require(home.Controls.Find("SharingOptions",true).Single().Visible,"Expanded sharing state should survive navigation.");
            Require(Descendants(home).OfType<ComboBox>().Last().SelectedIndex==2,"Quality should survive navigation.");
            home.Controls.Find("ToggleSharing",true).OfType<Button>().Single().PerformClick();
            foreach(string page in new[]{"recent","favorites","contacts"}){Navigate(home,page);Capture(home,page);}
            Navigate(home,"home");home.Size=home.MinimumSize;Application.DoEvents();Capture(home,"home-minimum");
            home.Controls.Find("ToggleSharing",true).OfType<Button>().Single().PerformClick();Application.DoEvents();Capture(home,"sharing-minimum");CaptureBottom(home,"sharing-minimum-bottom");
            foreach(string page in new[]{"recent","favorites","contacts","incoming"}){Navigate(home,page);Capture(home,page+"-minimum");CaptureBottom(home,page+"-minimum-bottom");}
            using(var dialog=home.ContactDialog(profile.Contacts[1])){Show(dialog);Capture(dialog,"contact-editor");dialog.Close();}
            using(var settings=new ServiceSettingsForm(profile)){Show(settings);Capture(settings,"settings");settings.Size=settings.MinimumSize;Application.DoEvents();Capture(settings,"settings-minimum");CaptureBottom(settings,"settings-minimum-bottom");settings.Close();}
            using(var approval=new ApprovalForm("Design team's very long workstation name for layout verification")){
                Show(approval);Capture(approval,"approval");
                Require(!Descendants(approval).OfType<CheckBox>().Any(c=>c.Checked),"Clipboard and file permissions must default off.");
                Descendants(approval).OfType<Button>().Single(b=>b.Text=="Allow viewing").PerformClick();
                var answer=approval.Answer.GetAwaiter().GetResult();Require(answer.Accept&&!answer.Control&&!answer.Clipboard&&!answer.Files,"Allow viewing must grant only viewing.");
            }
            using(var approval=new ApprovalForm("Unexpected viewer")){
                Show(approval);
                typeof(Form).GetMethod("ProcessDialogKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(approval,[Keys.Escape]);
                Require(approval.Answer.IsCompleted&&!approval.Answer.GetAwaiter().GetResult().Accept,"Escape must decline an approval request.");
            }
            var sent=new List<string>();
            using(var chat=new ChatForm("Studio workstation",text=>{sent.Add(text);return Task.CompletedTask;})){
                Show(chat);chat.Append("Remote","Hi Alex. Could you help me with this computer?");chat.Append("You","Of course. I'm connected in view-only mode.");
                Capture(chat,"chat");var message=Descendants(chat).OfType<TextBox>().Single();message.Text="Thanks";
                typeof(Control).GetMethod("OnKeyDown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(message,[new KeyEventArgs(Keys.Enter)]);
                Require(sent.SequenceEqual(new[]{"Thanks"})&&message.Text=="","Enter in chat should send once and clear the composer.");
                chat.Size=chat.MinimumSize;Application.DoEvents();Capture(chat,"chat-minimum");chat.Close();
            }
            using(var viewer=new ViewerForm(profile,"234567891",()=>false,false)){
                Show(viewer);Capture(viewer,"viewer");viewer.Size=viewer.MinimumSize;Application.DoEvents();Capture(viewer,"viewer-minimum");
                var toolbar=viewer.Controls.Find("SessionToolbar",true).OfType<ToolStrip>().Single();
                Require(toolbar.CanOverflow&&toolbar.OverflowButton.Visible,"Narrow viewers must offer toolbar overflow.");
                Require(RemoteInputFilter.IsProtected(toolbar.OverflowButton.DropDown),"Viewer overflow must reject injected remote input.");
                var record=toolbar.Items.OfType<ToolStripButton>().Single(b=>b.Text=="Record");record.Text="Stop recording";
                Require(record.AccessibilityObject.Name=="Stop recording","Recording's accessible action must follow its visible label.");record.Text="Record";
                Require(toolbar.Items.OfType<ToolStripButton>().Select(b=>b.Text).SequenceEqual(new[]{"Chat","Send file","Send clipboard","Screenshot","Record","Fullscreen","Stats","Disconnect"}),"Every session action must remain available.");viewer.Close();
            }
            profile.Contacts.Clear();Navigate(home,"home");Capture(home,"home-empty");
            foreach(string page in new[]{"recent","favorites","contacts"}){Navigate(home,page);Capture(home,page+"-empty");}
            home.Close();
            using(var fresh=new MainForm(false,new ClientSettings{StorageFile=Path.Combine(Folder,"fresh.json")})){
                Show(fresh);Require(!fresh.Controls.Find("CopyAddress",true).Single().Enabled,"Unregistered devices must not offer copying an address.");Capture(fresh,"home-unregistered");fresh.Close();
            }
            File.WriteAllText(Path.Combine(Folder,"result.txt"),$"PASS {screenshots} native renders; collapsed sharing, navigation, retained DPI sizes/settings, permissions, keyboard traversal/Enter/Escape, chat, protected toolbar overflow and text contrast. Actual DPI: {home.DeviceDpi} ({home.DeviceDpi*100/96d:F0}%). No physical DPI coverage beyond the current monitor configuration.");
        }
        catch(Exception ex){Environment.ExitCode=1;File.WriteAllText(Path.Combine(Folder,"result.txt"),"FAIL "+ex);}
    }
    private static void Show(Form form){form.Opacity=0;form.ShowInTaskbar=false;form.Show();Application.DoEvents();}
    private static void Navigate(MainForm form,string page){form.Controls.Find("Nav-"+page,true).OfType<Button>().Single().PerformClick();Application.DoEvents();Require(!Descendants(form).Any(c=>c.IsDisposed),"Page navigation disposed a retained control.");}
    private static void Capture(Form form,string name){
        form.PerformLayout();Application.DoEvents();
        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(Folder,name+".png"));}
        CheckLayout(form);screenshots++;
    }
    private static void CaptureBottom(Form form,string name){
        var scroll=Descendants(form).OfType<ScrollableControl>().FirstOrDefault(c=>c.AutoScroll&&c.VerticalScroll.Visible);
        if(scroll is null)return;
        var last=scroll.Controls.Cast<Control>().Last(c=>c.Visible);scroll.ScrollControlIntoView(last);Application.DoEvents();
        Require(last.Top>=0&&last.Bottom<=scroll.ClientSize.Height,"Scrolling must reveal the last section.");Capture(form,name);scroll.AutoScrollPosition=Point.Empty;Application.DoEvents();
    }
    private static void CheckLayout(Control parent){
        if(parent is ToolStrip)return;
        var children=parent.Controls.Cast<Control>().Where(c=>c.Visible).ToArray();
        foreach(var child in children){
            Require(child.Width>0&&child.Height>0,$"Zero-size control {child.Name} in {parent.Name}");
            if(parent is ScrollableControl scroll)Require(!scroll.HorizontalScroll.Visible,$"Horizontal scrolling in {parent.Name} {parent.ClientSize}; children: "+string.Join("; ",children.Select(c=>$"{c.Name} {c.Bounds} max {c.MaximumSize}")));
            Require(child.Left>=0&&child.Right<=parent.ClientSize.Width+1,$"Horizontal clipping: {child.Name} '{child.Text}' {child.Bounds} in {parent.Name} {parent.ClientSize}");
            foreach(var sibling in children.Where(c=>c!=child))Require(!child.Bounds.IntersectsWith(sibling.Bounds),$"Overlapping controls: '{child.Text}' and '{sibling.Text}' in {parent.Name}");
            CheckLayout(child);
        }
    }
    private static IEnumerable<Control> Descendants(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(var descendant in Descendants(child))yield return descendant;}}
    private static void CheckContrast(Color foreground,Color background){
        static double L(Color c){static double C(byte v){double n=v/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4);}return .2126*C(c.R)+.7152*C(c.G)+.0722*C(c.B);}
        double a=L(foreground),b=L(background);Require((Math.Max(a,b)+.05)/(Math.Min(a,b)+.05)>=4.5,$"Text contrast below 4.5:1: {foreground} on {background}");
    }
    private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
