using System.Drawing.Drawing2D;

namespace OpenDesk.App;

internal static class Theme
{
    internal static readonly Color Ink = Color.FromArgb(41,45,50), Accent = Color.FromArgb(201,39,49),
        Paper = Color.FromArgb(247,248,250), Sidebar = Color.FromArgb(240,242,245),
        Muted = Color.FromArgb(96,101,111), Border = Color.FromArgb(207,211,218),
        Selection = Color.FromArgb(253,235,237), Success = Color.FromArgb(35,122,67);
    internal static Label Label(string text, float size = 10, bool bold = false) => new() {
        Text=text, AutoSize=true, ForeColor=Ink, Margin=new Padding(0,4,0,8),
        Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular), UseMnemonic=false
    };
    internal static Label Description(string text) { var label=Label(text);label.ForeColor=Muted;return label; }
    internal static Button Button(string text, bool primary = false) {
        var button=new Button {
            Text=text,AutoSize=true,AutoEllipsis=true,
            Height=40, MinimumSize=new Size(104,40), Padding=new Padding(16,6,16,6),
            FlatStyle=FlatStyle.Flat, BackColor=primary?Accent:Color.White, ForeColor=primary?Color.White:Ink,
            Font=new Font("Segoe UI",10), Cursor=Cursors.Hand, Margin=new Padding(0,4,8,4), UseVisualStyleBackColor=false,UseMnemonic=false
        };
        button.FlatAppearance.BorderColor=primary?Accent:Border;
        button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(173,28,39):Sidebar;
        button.FlatAppearance.MouseDownBackColor=primary?Color.FromArgb(148,23,32):Border;
        return button;
    }
    internal static void Destructive(Button button) { button.ForeColor=Accent;button.FlatAppearance.BorderColor=Accent; }
    internal static void List(ListView list) {
        list.BorderStyle=BorderStyle.FixedSingle;list.BackColor=Color.White;list.ForeColor=Ink;
        list.Font=new Font("Segoe UI",10);list.HeaderStyle=ColumnHeaderStyle.Nonclickable;list.AccessibleName="Devices";
        list.SizeChanged+=(_,_)=> {
            if(list.Columns.Count==0)return;
            int width=Math.Max(1,list.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4),remaining=width;
            for(int i=list.Columns.Count-1;i>0;i--){list.Columns[i].Width=width/(list.Columns.Count+1);remaining-=list.Columns[i].Width;}
            list.Columns[0].Width=Math.Max(1,remaining);
        };
    }
    internal static FlowLayoutPanel Row(params Control[] controls) {
        var row=new FlowLayoutPanel {AutoSize=true,WrapContents=true,Margin=new Padding(0,0,0,8)};
        row.Controls.AddRange(controls);return row;
    }
    internal static Panel Stack()=>new VerticalStack();
    internal static Panel Card() {
        var card=Stack();card.Dock=DockStyle.None;card.AutoScroll=false;card.AutoSize=true;card.AutoSizeMode=AutoSizeMode.GrowAndShrink;card.Padding=new(24,16,24,16);card.Margin=new(0,8,0,16);
        card.Paint+=(_,e)=>{using var pen=new Pen(Border);e.Graphics.DrawRectangle(pen,0,0,card.ClientSize.Width-1,card.ClientSize.Height-1);};return card;
    }
    // Logical coordinates keep line artwork sharp when a window changes monitors.
    internal static void DrawIcon(Graphics graphics,string icon,Rectangle bounds,Color color) {
        var saved=graphics.Save();graphics.SmoothingMode=SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.X,bounds.Y);graphics.ScaleTransform(bounds.Width/24f,bounds.Height/24f);
        using var pen=new Pen(color,1.7f){LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round};
        switch(icon){
            case "home":graphics.DrawLine(pen,5,12,19,12);graphics.DrawLine(pen,12,5,12,19);break;
            case "recent":graphics.DrawEllipse(pen,3,3,18,18);graphics.DrawLines(pen,[new(12,7),new(12,12),new(16,14)]);break;
            case "favorites":graphics.DrawPolygon(pen,Enumerable.Range(0,10).Select(i=>{double a=-Math.PI/2+i*Math.PI/5,r=i%2==0?10:4.5;return new PointF(12+(float)(Math.Cos(a)*r),12+(float)(Math.Sin(a)*r));}).ToArray());break;
            case "contacts":graphics.DrawRectangle(pen,5,3,15,18);graphics.DrawEllipse(pen,10,6,5,5);graphics.DrawArc(pen,8,12,9,7,180,180);graphics.DrawLine(pen,2,7,6,7);graphics.DrawLine(pen,2,16,6,16);break;
            case "incoming":graphics.DrawLines(pen,[new(3,7),new(20,7),new(16,3)]);graphics.DrawLines(pen,[new(21,17),new(4,17),new(8,21)]);break;
            case "settings":graphics.DrawLine(pen,3,7,21,7);graphics.DrawLine(pen,3,17,21,17);graphics.DrawRectangle(pen,7,4,4,6);graphics.DrawRectangle(pen,14,14,4,6);break;
            case "brand":graphics.DrawPolygon(pen,[new(3,12),new(12,3),new(21,12),new(12,21)]);break;
            default:graphics.DrawRectangle(pen,2,3,20,14);graphics.DrawLine(pen,12,17,12,21);graphics.DrawLine(pen,7,21,17,21);break;
        }
        graphics.Restore(saved);
    }
    internal sealed class NavigationButton:Button {
        internal string Icon="home";
        internal bool Selected;
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);float scale=DeviceDpi/96f;int U(int v)=>(int)Math.Round(v*scale);
            DrawIcon(e.Graphics,Icon,new Rectangle(U(12),(Height-U(20))/2,U(20),U(20)),Enabled?ForeColor:SystemColors.GrayText);
            if(Selected){using var brush=new SolidBrush(Accent);e.Graphics.FillRectangle(brush,0,0,U(3),Height);}
        }
    }
    internal sealed class DeviceCard:Button {
        private readonly Contact contact;
        internal DeviceCard(Contact contact){
            this.contact=contact;AutoSize=false;Size=new(244,132);Margin=new(0,0,16,16);FlatStyle=FlatStyle.Flat;
            FlatAppearance.BorderColor=Border;FlatAppearance.MouseOverBackColor=Sidebar;FlatAppearance.MouseDownBackColor=Selection;
            BackColor=Color.White;Cursor=Cursors.Hand;AccessibleName=$"Connect to {contact.Name}, {OpenDesk.Core.DeviceProof.FormatId(contact.Id)}";AccessibleRole=AccessibleRole.PushButton;
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);float scale=DeviceDpi/96f;int U(int v)=>(int)Math.Round(v*scale);
            DrawIcon(e.Graphics,"device",new(U(16),U(16),U(26),U(26)),Muted);
            if(contact.Favorite)DrawIcon(e.Graphics,"favorites",new(Width-U(32),U(16),U(16),U(16)),Accent);
            using var nameFont=new Font("Segoe UI",10,FontStyle.Bold);using var addressFont=new Font("Segoe UI",10);
            TextRenderer.DrawText(e.Graphics,contact.Name,nameFont,new Rectangle(U(16),U(54),Width-U(32),U(26)),Ink,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics,OpenDesk.Core.DeviceProof.FormatId(contact.Id),addressFont,new Rectangle(U(16),U(84),Width-U(32),U(24)),Muted,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
        }
    }
    private sealed class VerticalStack:Panel {
        private bool arranging;
        internal VerticalStack(){Name="ContentStack";Dock=DockStyle.Fill;AutoScroll=true;Padding=new(24);BackColor=Color.White;DoubleBuffered=true;}
        protected override void OnLayout(LayoutEventArgs levent){
            if(arranging)return;arranging=true;
            try{int width=Math.Max(1,ClientSize.Width-Padding.Horizontal-(AutoScroll&&!VerticalScroll.Visible?SystemInformation.VerticalScrollBarWidth:0)),y=Padding.Top+AutoScrollPosition.Y;
                foreach(Control child in Controls){if(!child.Visible)continue;int available=Math.Max(1,width-child.Margin.Horizontal);child.Dock=DockStyle.None;
                    if(child is System.Windows.Forms.Label or Panel)child.MaximumSize=new(available,0);if(child is FlowLayoutPanel)child.AutoSize=false;
                    child.Width=available;int height=child is System.Windows.Forms.Label or FlowLayoutPanel or VerticalStack||child.AutoSize?child.GetPreferredSize(new Size(available,0)).Height:child.Height;
                    y+=child.Margin.Top;child.SetBounds(Padding.Left+child.Margin.Left,y,available,Math.Max(1,height));y+=Math.Max(1,height)+child.Margin.Bottom;
                }
                if(AutoScroll)AutoScrollMinSize=new(0,Math.Max(0,y-AutoScrollPosition.Y+Padding.Bottom));base.OnLayout(levent);
            }finally{arranging=false;}
        }
        public override Size GetPreferredSize(Size proposedSize){int width=proposedSize.Width>0?proposedSize.Width:Width,available=Math.Max(1,width-Padding.Horizontal),height=Padding.Vertical;
            foreach(Control child in Controls){if(!child.Visible)continue;var size=child.GetPreferredSize(new Size(Math.Max(1,available-child.Margin.Horizontal),0));height+=(child is System.Windows.Forms.Label or FlowLayoutPanel or VerticalStack||child.AutoSize?size.Height:child.Height)+child.Margin.Vertical;}return new(width,height);
        }
    }
}
