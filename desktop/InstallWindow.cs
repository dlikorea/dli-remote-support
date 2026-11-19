using System.Drawing;
using System.Windows.Forms;

internal sealed class InstallWindow : Form
{
    public InstallWindow()
    {
        Text = Product.Title + " 설치";
        Icon = Brand.LoadIcon();
        Font = new Font("Malgun Gothic", 10F);
        AutoScaleMode = AutoScaleMode.None;
        float scale = System.Math.Max(1F, Font.Height / 17F);
        int pad = (int)(20 * scale);
        var workArea = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(System.Math.Min((int)(428 * scale), workArea.Width - 24),
            System.Math.Min((int)(220 * scale), workArea.Height - 60));
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(24, 38, 57);

        var logo = new PictureBox { Image = Brand.LoadMark(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size((int)(54 * scale), (int)(54 * scale)), AccessibleName = "디엘아이 로고" };
        var heading = new Label { Text = Product.Title, Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            AutoSize = true };
        var version = new Label { Text = "버전 " + Product.Release.ToString(), ForeColor = Color.FromArgb(65, 85, 110),
            AutoSize = true };
        var detail = new Label { Text = "이 Windows 계정에 원격지원 프로그램을 설치합니다.",
            AutoSize = true, MaximumSize = new Size(ClientSize.Width - pad * 2 - 10, 0), Margin = new Padding(0, pad / 2, 0, pad / 2) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel,
            AutoSize = true, MinimumSize = new Size((int)(82 * scale), (int)(36 * scale)) };
        var install = new Button { Text = "설치", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(24, 77, 143), ForeColor = Color.White, AutoSize = true,
            MinimumSize = new Size((int)(94 * scale), (int)(36 * scale)) };
        install.FlatAppearance.BorderSize = 0;
        AcceptButton = install;
        CancelButton = cancel;
        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        header.Controls.Add(logo, 0, 0);
        header.SetRowSpan(logo, 2);
        header.Controls.Add(heading, 1, 0);
        header.Controls.Add(version, 1, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { install, cancel });
        var content = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, Padding = new Padding(pad) };
        content.Controls.Add(header);
        content.Controls.Add(detail);
        content.Controls.Add(buttons);
        Controls.Add(content);
        var icon = Icon;
        Disposed += (sender, args) => { logo.Image.Dispose(); icon.Dispose(); };
    }
}
