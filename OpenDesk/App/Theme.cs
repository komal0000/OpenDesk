namespace OpenDesk.App;

internal static class Theme
{
    internal static readonly Color Ink = Color.FromArgb(46, 48, 52), Green = Color.FromArgb(201, 39, 49), Paper = Color.FromArgb(247, 247, 248);
    internal static Label Label(string text, float size = 10, bool bold = false) => new() {
        Text = text, AutoSize = true, ForeColor = Ink, Margin = new Padding(0, 8, 0, 8),
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };
    internal static Button Button(string text, bool primary = false) => new() {
        Text = text, AutoSize = true, Height = 38, MinimumSize = new Size(110, 38), Padding = new Padding(12, 3, 12, 3),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Green : Color.White, ForeColor = primary ? Color.White : Ink,
        Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand, Margin = new Padding(0, 4, 10, 4), UseVisualStyleBackColor=false
    };
    internal static FlowLayoutPanel Row(params Control[] controls) {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 4, 0, 8) };
        row.Controls.AddRange(controls); return row;
    }
    internal static Panel Stack() => new VerticalStack();
    private sealed class VerticalStack : Panel
    {
        private bool arranging;
        internal VerticalStack(){Name="ContentStack";Dock=DockStyle.Fill;AutoScroll=true;Padding=new(24);BackColor=Color.White;DoubleBuffered=true;}
        protected override void OnControlAdded(ControlEventArgs e){e.Control!.Dock=DockStyle.None;base.OnControlAdded(e);}
        protected override void OnLayout(LayoutEventArgs levent){
            if(arranging)return;arranging=true;
            try{int width=Math.Max(1,ClientSize.Width-Padding.Horizontal-(AutoScroll?SystemInformation.VerticalScrollBarWidth:0));int y=Padding.Top+AutoScrollPosition.Y;
                foreach(Control child in Controls){int available=Math.Max(1,width-child.Margin.Horizontal);child.Dock=DockStyle.None;
                    if(child is System.Windows.Forms.Label or Panel)child.MaximumSize=new(available,0);
                    if(child is FlowLayoutPanel)child.AutoSize=false;
                    child.Width=available;int height=child is System.Windows.Forms.Label or FlowLayoutPanel or VerticalStack?child.GetPreferredSize(new Size(available,0)).Height:child.Height;
                    y+=child.Margin.Top;child.SetBounds(Padding.Left+child.Margin.Left,y,available,Math.Max(1,height));y+=Math.Max(1,height)+child.Margin.Bottom;
                }
                if(AutoScroll)AutoScrollMinSize=new(0,Math.Max(0,y-AutoScrollPosition.Y+Padding.Bottom));base.OnLayout(levent);
            }finally{arranging=false;}
        }
        public override Size GetPreferredSize(Size proposedSize){int width=proposedSize.Width>0?proposedSize.Width:Width;int available=Math.Max(1,width-Padding.Horizontal);int height=Padding.Vertical;
            foreach(Control child in Controls){var size=child.GetPreferredSize(new Size(Math.Max(1,available-child.Margin.Horizontal),0));height+=(child is System.Windows.Forms.Label or FlowLayoutPanel or VerticalStack?size.Height:child.Height)+child.Margin.Vertical;}return new(width,height);}
    }
}
