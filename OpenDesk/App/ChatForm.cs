namespace OpenDesk.App;

internal sealed class ChatForm : Form
{
    private readonly RichTextBox history=new(){ReadOnly=true,Dock=DockStyle.Fill,BackColor=Color.White,BorderStyle=BorderStyle.None};
    private readonly TextBox message=new(){Dock=DockStyle.Fill,MaxLength=2000};
    internal ChatForm(string title,Func<string,Task> send)
    {
        AutoScaleDimensions=new(96,96);AutoScaleMode=AutoScaleMode.Dpi;Text="OpenDesk · Chat · " + title;ClientSize=new(460,420);MinimumSize=new(340,280);Font=new Font("Segoe UI",10);
        var row=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=58,ColumnCount=2,Padding=new Padding(8)};row.ColumnStyles.Add(new(SizeType.Percent,100));row.ColumnStyles.Add(new(SizeType.Absolute,96));row.RowStyles.Add(new(SizeType.Percent,100));
        var button=Theme.Button("Send",true);button.MinimumSize=Size.Empty;button.AutoSize=false;button.Dock=DockStyle.Fill;button.Margin=new(8,0,0,0);row.Controls.Add(message,0,0);row.Controls.Add(button,1,0);Controls.Add(history);Controls.Add(row);
        button.Click+=async(_,_)=>await Send();message.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await Send();}};
        async Task Send(){if(!button.Enabled)return;string text=message.Text.Trim();if(text.Length==0)return;button.Enabled=false;try{await send(text);Append("You",text);message.Clear();}catch(Exception ex){Append("System",ex.Message);}finally{button.Enabled=true;}}
    }
    internal void Append(string from,string text)
    {
        if(IsDisposed)return;if(InvokeRequired){BeginInvoke(()=>Append(from,text));return;}
        if(history.TextLength>100_000)history.Clear();history.AppendText(from+": "+text+Environment.NewLine);history.SelectionStart=history.TextLength;history.ScrollToCaret();
    }
}
