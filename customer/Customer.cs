using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

#if !DLI_INSTALLED
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        NativeInput.SetProcessDPIAware();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 12;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CustomerWindow(ReadServer(args)));
    }

    private static string ReadServer(string[] args)
    {
        if (args.Length == 2 && args[0] == "--server")
            return args[1];

        string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server-url.txt");
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length < 4096)
                return File.ReadAllText(file).Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "http://127.0.0.1:8765";
    }
}
#endif

internal sealed class CustomerWindow : Form
{
    private const string ConsentText =
        "코드를 알려 주면 상담원이 화면을 조작하고, 이 PC에서 사용할 수 있는 드라이브의 파일을 가져올 수 있습니다.";

    private readonly Label heading;
    private readonly Label explanation;
    private readonly Label status;
    private readonly Label serverLabel;
    private readonly TextBox serverBox;
    private readonly bool lockServer;
    private readonly TextBox codeBox;
    private readonly Button startButton;
    private readonly Button copyButton;
    private readonly Button endButton;
    private readonly Button saveFileButton;
    private readonly Label inboxLabel;
    private readonly ListBox inbox;
    private readonly Label memoLabel;
    private readonly TextBox memo;
    private Form fileWindow;
    private readonly NativeInput nativeInput = new NativeInput();
    private bool layingOut;
    private string layoutScreen;
    private Session session;
    private bool active;
    private bool ending;
    private bool closing;
    private bool closeReady;
    private bool fileBusy;
    private bool dialogOpen;
    private bool discardNextInput;
    private int dialogGeneration;
    private Task frameUpload = Task.FromResult(false);
    private HostState monitorState;
    private Rectangle frameArea;
    private int frameMonitorRevision;

    public CustomerWindow(string server, bool lockServer = false)
    {
        this.lockServer = lockServer;
        Text = "디엘아이 원격지원";
        Font = new Font("Malgun Gothic", 10F);
        AutoScaleMode = AutoScaleMode.None;
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(24, 38, 57);
#if DLI_INSTALLED
        Icon = Brand.LoadIcon();
#else
        Icon = SystemIcons.Application;
#endif

        heading = MakeLabel("디엘아이 원격지원", 24, 20, 408, 31);
        heading.Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
        explanation = MakeLabel(ConsentText, 24, 61, 408, 50);
        serverLabel = MakeLabel("지원 서버", 24, 118, 408, 23);
        serverBox = new TextBox { Bounds = new Rectangle(24, 143, 408, 28), Text = server, MaxLength = 2048,
            AccessibleName = "지원 서버 주소" };
        startButton = MakeButton("지원 코드 만들기", 24, 187, 408, 39, Color.FromArgb(24, 77, 143));
        codeBox = new TextBox
        {
            Bounds = new Rectangle(24, 188, 158, 39),
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Center,
            ReadOnly = true,
            Visible = false,
            AccessibleName = "지원 코드"
        };
        copyButton = MakeButton("코드 복사", 190, 187, 104, 39, Color.FromArgb(24, 77, 143));
        endButton = MakeButton("지원 종료", 302, 187, 130, 39, Color.FromArgb(166, 37, 40));
        memoLabel = MakeLabel("상담원 메모", 20, 16, 416, 25);
        memo = new TextBox { Bounds = new Rectangle(20, 45, 416, 107), Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(245, 248, 250), AccessibleName = "상담원 메모" };
        inboxLabel = MakeLabel("상담원이 보낸 파일을 저장할 수 있습니다.", 20, 15, 382, 26);
        inbox = new ListBox { Bounds = new Rectangle(20, 49, 382, 76), AccessibleName = "받은 파일",
            HorizontalScrollbar = true, IntegralHeight = false };
        saveFileButton = MakeButton("선택 파일 저장", 248, 135, 154, 36, Color.FromArgb(24, 77, 143));
        status = MakeLabel("디엘아이 상담원과 통화 중일 때 사용하세요.", 24, 248, 408, 57);
        status.ForeColor = Color.FromArgb(65, 85, 110);

        Controls.AddRange(new Control[] { heading, explanation, serverLabel, serverBox,
            startButton, codeBox, copyButton, endButton, memoLabel, memo, status });
        memoLabel.Visible = false;
        memo.Visible = false;
        copyButton.Visible = false;
        endButton.Visible = false;
        serverBox.Visible = !lockServer;
        serverLabel.Visible = !lockServer;
        LayoutWindow(Screen.PrimaryScreen);
        AcceptButton = startButton;
        startButton.Click += async (sender, eventArgs) => await StartAsync();
        copyButton.Click += CopyCode;
        endButton.Click += async (sender, eventArgs) => await StopAsync("원격지원을 종료했습니다.");
        saveFileButton.Click += async (sender, eventArgs) => await SaveFileAsync();
        inbox.SelectedIndexChanged += (sender, eventArgs) => UpdateFileButtons();
        FormClosing += CloseWindow;
        LocationChanged += (sender, eventArgs) => {
            if (!Visible || layingOut) return;
            Screen screen = Screen.FromControl(this);
            if (screen.DeviceName != layoutScreen) LayoutWindow(screen);
        };
        Shown += (sender, eventArgs) => {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            LayoutWindow(Screen.FromControl(this));
        };
        FormClosed += (sender, eventArgs) => {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        };
    }

    private static Label MakeLabel(string text, int x, int y, int width, int height)
    {
        return new Label { Text = text, Bounds = new Rectangle(x, y, width, height) };
    }

    private static Button MakeButton(string text, int x, int y, int width, int height, Color color)
    {
        var button = new Button
        {
            Text = text, Bounds = new Rectangle(x, y, width, height),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = color
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private int Unit(int amount)
    {
        return (int)Math.Round(amount * Math.Max(1.0, Font.Height / 17.0));
    }

    private int PlaceLabel(Label label, int x, int y, int width)
    {
        int height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, Int32.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Unit(5);
        label.SetBounds(x, y, width, height);
        return y + height;
    }

    private int ButtonWidth(Button button)
    {
        return TextRenderer.MeasureText(button.Text, button.Font).Width + Unit(24);
    }

    private void LayoutWindow(Screen screen, bool positionAtTop = false)
    {
        if (screen == null || layingOut) return;
        layingOut = true;
        layoutScreen = screen.DeviceName;
        SuspendLayout();
        try
        {
            AutoScrollPosition = Point.Empty;
            Rectangle work = screen.WorkingArea;
            Size chrome = SizeFromClientSize(Size.Empty);
            int pad = Unit(18);
            int gap = Unit(9);
            int width = Math.Max(180, Math.Min(Unit(472), work.Width - chrome.Width - 24));
            int availableHeight = Math.Max(150, work.Height - chrome.Height - 24);
            int height = PositionContent(width, pad, gap, availableHeight);
            if (height > availableHeight)
                height = PositionContent(width - SystemInformation.VerticalScrollBarWidth, pad, gap, availableHeight);
            ClientSize = new Size(width, Math.Min(height, availableHeight));
            AutoScrollMinSize = height > availableHeight ? new Size(0, height) : Size.Empty;
            if (positionAtTop)
                Location = new Point(Math.Max(work.Left, work.Right - Width - 12), work.Top + 12);
            else if (Visible)
                Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)),
                    Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
        }
        finally
        {
            ResumeLayout();
            layingOut = false;
        }
    }

    private int PositionContent(int width, int pad, int gap, int availableHeight)
    {
        int inner = width - pad * 2;
        int y = pad;
        int buttonHeight = Math.Max(Unit(38), Font.Height + Unit(14));
        if (active)
        {
            y = PlaceLabel(memoLabel, pad, y, inner) + gap;
            int memoHeight = Math.Max(Font.Height + Unit(8), Math.Min(Unit(98), availableHeight - y - gap - buttonHeight - pad));
            memo.SetBounds(pad, y, inner, memoHeight);
            int endWidth = Math.Min(inner, Math.Max(Unit(118), ButtonWidth(endButton)));
            endButton.SetBounds(width - pad - endWidth, memo.Bottom + gap, endWidth, buttonHeight);
            return endButton.Bottom + pad;
        }

        y = PlaceLabel(heading, pad, y, inner) + gap;
        y = PlaceLabel(explanation, pad, y, inner) + gap;
        if (!lockServer)
        {
            y = PlaceLabel(serverLabel, pad, y, inner);
            serverBox.SetBounds(pad, y, inner, serverBox.PreferredHeight);
            y = serverBox.Bottom + gap;
        }
        if (endButton.Visible || codeBox.Text.Length > 0)
        {
            int copyWidth = ButtonWidth(copyButton);
            int endWidth = ButtonWidth(endButton);
            int codeWidth = TextRenderer.MeasureText("00000000", codeBox.Font).Width + Unit(22);
            if (inner < codeWidth + copyWidth + endWidth + gap * 2)
            {
                codeBox.SetBounds(pad, y, inner, codeBox.PreferredHeight);
                y = codeBox.Bottom + gap;
                int first = (inner - gap) / 2;
                if (first < Math.Max(copyWidth, endWidth))
                {
                    copyButton.SetBounds(pad, y, inner, buttonHeight);
                    y = copyButton.Bottom + gap;
                    endButton.SetBounds(pad, y, inner, buttonHeight);
                }
                else
                {
                    copyButton.SetBounds(pad, y, first, buttonHeight);
                    endButton.SetBounds(pad + first + gap, y, inner - first - gap, buttonHeight);
                }
            }
            else
            {
                codeBox.SetBounds(pad, y + Math.Max(0, (buttonHeight - codeBox.PreferredHeight) / 2),
                    inner - copyWidth - endWidth - gap * 2, codeBox.PreferredHeight);
                endButton.SetBounds(width - pad - endWidth, y, endWidth, buttonHeight);
                copyButton.SetBounds(endButton.Left - gap - copyWidth, y, copyWidth, buttonHeight);
            }
        }
        else startButton.SetBounds(pad, y, inner, buttonHeight);
        y += buttonHeight + gap;
        return PlaceLabel(status, pad, y, inner) + pad;
    }

    private void DisplayChanged(object sender, EventArgs args)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed) LayoutWindow(Screen.FromControl(this)); })); }
        catch (InvalidOperationException) { }
    }

    private async Task StartAsync()
    {
        if (session != null || ending || closing)
            return;

        Uri server;
        if (!Uri.TryCreate(serverBox.Text.Trim(), UriKind.Absolute, out server)
            || (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && server.IsLoopback))
            || server.AbsolutePath != "/" || server.UserInfo.Length > 0
            || server.Query.Length > 0 || server.Fragment.Length > 0)
        {
            status.Text = "HTTPS 서버 주소를 입력하세요. 이 PC에서 시험할 때는 127.0.0.1의 HTTP 주소를 사용할 수 있습니다.";
            LayoutWindow(Screen.FromControl(this));
            serverBox.Focus();
            return;
        }

        var current = new Session(server);
        frameUpload = Task.FromResult(false);
        monitorState = null;
        frameArea = Rectangle.Empty;
        frameMonitorRevision = 0;
        discardNextInput = false;
        session = current;
        startButton.Enabled = false;
        serverBox.Enabled = false;
        status.Text = "지원 코드를 만드는 중입니다.";
        LayoutWindow(Screen.FromControl(this));
        Task inputPump = null;
        Task framePump = null;
        Task filesPump = null;
        Task stop = null;
        try
        {
            SessionCode result = await current.Create();
            if (session != current)
                return;

            codeBox.Text = result.code;
            codeBox.Visible = true;
            copyButton.Visible = true;
            endButton.Visible = true;
            startButton.Visible = false;
            AcceptButton = null;
            status.Text = "이 코드를 상담원에게 알려 주세요. 상담원이 연결하면 원격지원이 시작됩니다.";
            LayoutWindow(Screen.FromControl(this));

            while (session == current)
            {
                HostState state = await current.GetState();
                if (session != current)
                    break;
                if (state.phase == "ended")
                    throw new SessionEndedException();
                if (state.phase == "controlling" && state.controlEpoch > 0)
                {
                    UpdateMonitorState(state);
                    ShowActive();
                    inputPump = ReceiveInput(current, state.controlEpoch);
                    framePump = SendFrames(current);
                    filesPump = PollFiles(current);
                    await (await Task.WhenAny(inputPump, framePump, filesPump));
                    break;
                }
                if (state.phase != "waiting")
                    throw new InvalidDataException();
                await Task.Delay(500, current.Token);
            }
        }
        catch (SessionEndedException)
        {
            if (session == current)
                stop = StopAsync("지원 시간이 만료되었거나 상담원이 연결을 종료했습니다.", false);
        }
        catch (Exception)
        {
            if (session == current)
                stop = StopAsync("연결 또는 화면 공유에 문제가 생겨 지원이 종료되었습니다. 다시 시도하려면 새 코드를 만드세요.");
        }
        finally
        {
            FinishSession(current, inputPump, framePump, filesPump, stop);
        }
    }

    private static async void FinishSession(Session current, params Task[] work)
    {
        foreach (Task task in work)
        {
            if (task == null)
                continue;
            try { await task; }
            catch (Exception) { }
        }
        current.Dispose();
    }

    private void ShowActive()
    {
        active = true;
        codeBox.Text = "";
        codeBox.Visible = false;
        copyButton.Visible = false;
        serverBox.Visible = false;
        serverLabel.Visible = false;
        heading.Visible = false;
        explanation.Visible = false;
        status.Visible = false;
        memo.Visible = true;
        memoLabel.Visible = true;
        UpdateMemo(null);
        LayoutWindow(Screen.PrimaryScreen, true);
        TopMost = true;
    }

    private async Task ReceiveInput(Session current, int epoch)
    {
        while (session == current)
        {
            InputBatch batch = await current.GetInput();
            if (session != current)
                return;
            if (batch.phase != "controlling" || batch.controlEpoch != epoch)
                throw new SessionEndedException();
            if (batch.events == null || batch.events.Length > 512)
                throw new InvalidDataException();

            UpdateMonitorState(batch);
            if (!NativeInput.IsInputDesktop())
                throw new InvalidOperationException();
            Rectangle area = monitorState == null ? ScreenCapture.PrimaryArea() : frameArea;
            if (monitorState != null && batch.events.Length > 0)
            {
                area = ScreenCapture.GetArea(monitorState.selectedMonitor);
                if (area.IsEmpty || area != frameArea || batch.monitorRevision != frameMonitorRevision
                    || batch.monitorRevision != monitorState.monitorRevision)
                {
                    nativeInput.ReleaseAll();
                    await Task.Delay(40, current.Token);
                    continue;
                }
            }
            if (!dialogOpen && !discardNextInput)
            {
                Rectangle panel = Bounds;
                foreach (RemoteEvent input in batch.events)
                    nativeInput.Apply(input, area, panel, NativeInput.GetForegroundWindow() == Handle
                        || (fileWindow != null && NativeInput.GetForegroundWindow() == fileWindow.Handle),
                        fileWindow == null ? Rectangle.Empty : fileWindow.Bounds);
            }
            if (!dialogOpen)
                discardNextInput = false;

            await Task.Delay(40, current.Token);
        }
    }

    private void UpdateMonitorState(HostState state)
    {
        if (!state.monitorSwitch || (monitorState != null && state.monitorRevision <= monitorState.monitorRevision))
            return;
        monitorState = state;
        frameArea = Rectangle.Empty;
        nativeInput.ReleaseAll();
    }

    private async Task SendFrames(Session current)
    {
        DisplayMonitor[] published = null;
        while (session == current)
        {
            if (dialogOpen)
            {
                await Task.Delay(100, current.Token);
                continue;
            }
            var interval = System.Diagnostics.Stopwatch.StartNew();
            int generation = dialogGeneration;
            if (monitorState != null)
            {
                DisplayMonitor[] monitors = ScreenCapture.GetMonitors();
                if (!ScreenCapture.SameMonitors(published, monitors))
                {
                    frameArea = Rectangle.Empty;
                    nativeInput.ReleaseAll();
                    HostState state = await current.SendMonitors(monitors);
                    if (session != current)
                        return;
                    published = monitors;
                    UpdateMonitorState(state);
                }
            }
            HostState selection = monitorState;
            Rectangle area = selection == null ? ScreenCapture.PrimaryArea() : ScreenCapture.GetArea(selection.selectedMonitor);
            if (area.IsEmpty)
            {
                await Task.Delay(125, current.Token);
                continue;
            }
            byte[] frame;
            try
            {
                frame = await Task.Run(() => ScreenCapture.Capture(area), current.Token);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (selection == null || area == ScreenCapture.GetArea(selection.selectedMonitor))
                    throw;
                continue;
            }
            if (session != current)
                return;
            if (!dialogOpen && generation == dialogGeneration && selection == monitorState
                && (selection == null || area == ScreenCapture.GetArea(selection.selectedMonitor)))
            {
                frameArea = area;
                frameMonitorRevision = selection == null ? 0 : selection.monitorRevision;
                frameUpload = current.SendFrame(frame, frameMonitorRevision);
                await frameUpload;
            }
            int pause = Math.Max(0, 125 - (int)interval.ElapsedMilliseconds);
            if (pause > 0)
                await Task.Delay(pause, current.Token);
        }
    }

    private async Task StopAsync(string message, bool notifyServer = true)
    {
        Session current = session;
        if (current == null)
            return;

        session = null;
        active = false;
        ending = true;
        current.Cancel();
        nativeInput.ReleaseAll();
        TopMost = false;
        heading.Visible = true;
        explanation.Visible = true;
        memo.Visible = false;
        memoLabel.Visible = false;
        memo.Clear();
        endButton.Visible = false;
        codeBox.Text = "";
        codeBox.Visible = false;
        copyButton.Visible = false;
        serverLabel.Visible = !lockServer;
        serverBox.Visible = !lockServer;
        startButton.Visible = true;
        if (fileWindow != null) fileWindow.Close();
        inbox.Items.Clear();
        status.Visible = true;
        status.Text = message;
        LayoutWindow(Screen.FromControl(this));

        if (notifyServer)
        {
            try { await current.End(); }
            catch (Exception) { }
        }
        ending = false;
        RestoreStartButton();
    }

    private void RestoreStartButton()
    {
        if (closing || ending || dialogOpen || fileBusy)
            return;
        serverBox.Enabled = true;
        startButton.Enabled = true;
        AcceptButton = startButton;
    }

    private void UpdateMemo(string value)
    {
        string message = String.IsNullOrEmpty(value) ? "상담원이 남긴 메모가 여기에 표시됩니다." : value.Replace("\n", "\r\n");
        if (memo.Text != message) memo.Text = message;
    }

    private void UpdateFileButtons()
    {
        saveFileButton.Enabled = active && !fileBusy && inbox.SelectedItem != null;
    }

    private async Task PollFiles(Session current)
    {
        Task fileWork = Task.FromResult(false);
        string requestId = null;
        var seenFiles = new HashSet<string>();
        try
        {
            while (session == current)
            {
                HostState state = await current.GetState();
                if (session != current) return;
                if (state.phase != "controlling") throw new SessionEndedException();
                UpdateMemo(state.memo);
                if (fileWork.IsCompleted)
                {
                    await fileWork;
                    if (state.fileRequest != null && state.fileRequest.id != requestId)
                    {
                        requestId = state.fileRequest.id;
                        fileWork = ProcessFileRequest(current, state.fileRequest);
                    }
                }
                Inbox result = await current.ListFiles();
                if (session != current) return;
                if (result.files == null || result.files.Length > 3) throw new InvalidDataException();
                UpdateInbox(result.files);
                bool newlyReceived = false;
                foreach (SharedFile file in result.files) newlyReceived |= seenFiles.Add(file.id);
                if (newlyReceived)
                {
                    inboxLabel.Text = "상담원이 보낸 파일을 저장할 수 있습니다.";
                    ShowReceivedFiles();
                }
                await Task.Delay(650, current.Token);
            }
        }
        finally { ObserveFileWork(fileWork); }
    }

    private static async void ObserveFileWork(Task task)
    {
        try { await task; }
        catch (Exception) { }
    }

    private async Task ProcessFileRequest(Session current, FileRequest request)
    {
        FileResponse result = new FileResponse { id = request.id };
        try
        {
            if (request.action == "drives")
                result = await Task.Run(() => CustomerFiles.ListDrives(request, current.Token), current.Token);
            else if (request.action == "list")
                result = await Task.Run(() => CustomerFiles.List(request, current.Token), current.Token);
            else if (request.action == "pull")
            {
                using (var file = await Task.Run(() => CustomerFiles.Open(request), current.Token))
                    await current.Upload(Path.GetFileName(file.Name), file);
            }
            else throw new IOException();
        }
        catch (FileTransferException error) { result.error = error.Message; }
        catch (Exception) { result.error = "고객 PC에서 해당 파일 또는 폴더를 열 수 없습니다."; }
        if (session == current) await current.ReportFile(result);
    }

    private void ShowReceivedFiles()
    {
        if (fileWindow != null) return;
        var window = new Form { Text = "받은 파일", FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
            Font = Font, Icon = Icon, AutoScaleMode = AutoScaleMode.None };
        window.ClientSize = new Size(Math.Min(Unit(422), Math.Max(220, Screen.FromControl(this).WorkingArea.Width - Unit(30))), Unit(200));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(Unit(12)) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inboxLabel.Dock = DockStyle.Top;
        inboxLabel.AutoSize = true;
        inboxLabel.MaximumSize = new Size(window.ClientSize.Width - Unit(32), 0);
        inbox.Dock = DockStyle.Fill;
        saveFileButton.AutoSize = true;
        saveFileButton.MinimumSize = new Size(Unit(130), Unit(34));
        saveFileButton.Anchor = AnchorStyles.Right;
        fileWindow = window;
        if (inbox.SelectedIndex < 0) inbox.SelectedIndex = 0;
        layout.Controls.Add(inboxLabel, 0, 0);
        layout.Controls.Add(inbox, 0, 1);
        layout.Controls.Add(saveFileButton, 0, 2);
        window.Controls.Add(layout);
        window.FormClosed += (sender, args) => {
            layout.Controls.Remove(inboxLabel);
            layout.Controls.Remove(inbox);
            layout.Controls.Remove(saveFileButton);
            fileWindow = null;
        };
        window.Show(this);
    }

    private void UpdateInbox(SharedFile[] files)
    {
        var previous = inbox.SelectedItem as SharedFile;
        if (files.Length == inbox.Items.Count)
        {
            bool equal = true;
            for (int index = 0; index < files.Length; index++)
                equal = equal && files[index].id == ((SharedFile)inbox.Items[index]).id;
            if (equal)
                return;
        }
        inbox.BeginUpdate();
        try
        {
            inbox.Items.Clear();
            foreach (SharedFile file in files)
            {
                if (!SharedFile.IsValid(file))
                    throw new InvalidDataException();
                inbox.Items.Add(file);
                if (previous != null && previous.id == file.id)
                    inbox.SelectedItem = file;
            }
            UpdateFileButtons();
        }
        finally { inbox.EndUpdate(); }
    }

    private async Task<DialogResult> ShowFileDialog(Session current, FileDialog dialog)
    {
        dialogOpen = true;
        dialogGeneration++;
        nativeInput.ReleaseAll();
        try
        {
            await frameUpload;
            await current.ClearFrame();
            if (session != current)
                return DialogResult.Cancel;
            return dialog.ShowDialog(this);
        }
        finally
        {
            discardNextInput = true;
            dialogOpen = false;
        }
    }

    private async Task SaveFileAsync()
    {
        Session current = session;
        var selected = inbox.SelectedItem as SharedFile;
        if (!active || current == null || selected == null || fileBusy)
            return;
        fileBusy = true;
        UpdateFileButtons();
        try
        {
            using (var dialog = new SaveFileDialog { Title = "받은 파일 저장", FileName = selected.name, AddExtension = false,
                OverwritePrompt = false, RestoreDirectory = true })
            {
                if (await ShowFileDialog(current, dialog) != DialogResult.OK || session != current)
                    return;
                if (File.Exists(dialog.FileName))
                    throw new FileTransferException("같은 이름의 파일이 있습니다. 다른 이름으로 저장하세요.");
                inboxLabel.Text = "받은 파일을 저장하는 중입니다.";
                byte[] contents = await current.Download(selected);
                if (session != current)
                    return;
                await WriteNewFile(dialog.FileName, contents, current.Token);
                if (session != current)
                    return;
                try
                {
                    await current.Dismiss(selected.id);
                    if (session == current)
                        inboxLabel.Text = "받은 파일을 저장했습니다.";
                }
                catch (Exception)
                {
                    if (session == current)
                        inboxLabel.Text = "파일을 저장했습니다. 수신 목록을 갱신하지 못했습니다.";
                }
            }
        }
        catch (FileTransferException error)
        {
            if (session == current)
                inboxLabel.Text = error.Message;
        }
        catch (Exception)
        {
            if (session == current)
                inboxLabel.Text = "파일을 저장하지 못했습니다. 경로와 연결 상태를 확인하세요.";
        }
        finally
        {
            fileBusy = false;
            UpdateFileButtons();
            if (session == null)
                RestoreStartButton();
        }
    }

    private static async Task WriteNewFile(string path, byte[] contents, CancellationToken token)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + ".dli-part");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await file.WriteAsync(contents, 0, contents.Length, token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void CopyCode(object sender, EventArgs eventArgs)
    {
        if (codeBox.Text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(codeBox.Text);
            status.Text = "지원 코드를 복사했습니다. 통화 중인 상담원에게 알려 주세요.";
        }
        catch (ExternalException)
        {
            status.Text = "복사할 수 없습니다. 화면에 표시된 지원 코드를 상담원에게 읽어 주세요.";
        }
        LayoutWindow(Screen.FromControl(this));
    }

    private async void CloseWindow(object sender, FormClosingEventArgs eventArgs)
    {
        if (closeReady || (session == null && !ending))
            return;
        eventArgs.Cancel = true;
        if (closing)
            return;
        closing = true;
        await StopAsync("원격지원을 종료했습니다.");
        closeReady = true;
        BeginInvoke(new Action(Close));
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason == SessionSwitchReason.SessionUnlock)
        {
            try { BeginInvoke(new Action(nativeInput.ReleaseAll)); }
            catch (InvalidOperationException) { }
            return;
        }
        if (eventArgs.Reason != SessionSwitchReason.SessionLock
            && eventArgs.Reason != SessionSwitchReason.SessionLogoff
            && eventArgs.Reason != SessionSwitchReason.ConsoleDisconnect
            && eventArgs.Reason != SessionSwitchReason.RemoteDisconnect)
            return;

        try
        {
            BeginInvoke(new Action(async () =>
                await StopAsync("Windows 세션이 잠기거나 전환되어 원격지원을 종료했습니다.")));
        }
        catch (InvalidOperationException) { }
    }
}

public class HostState
{
    public string memo { get; set; }
    public FileRequest fileRequest { get; set; }
    public string phase { get; set; }
    public int controlEpoch { get; set; }
    public bool monitorSwitch { get; set; }
    public string selectedMonitor { get; set; }
    public int monitorRevision { get; set; }
}

public sealed class DisplayMonitor
{
    public string id { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public bool primary { get; set; }
    internal Rectangle Bounds { get; set; }
}

public sealed class InputBatch : HostState
{
    public RemoteEvent[] events { get; set; }
}

public sealed class SessionCode
{
    public string code { get; set; }
    public string hostToken { get; set; }
}

public sealed class RemoteEvent
{
    public string type { get; set; }
    public double? x { get; set; }
    public double? y { get; set; }
    public int? button { get; set; }
    public double? delta { get; set; }
    public int? key { get; set; }
    public bool extended { get; set; }
    public string text { get; set; }
}

public sealed class Inbox
{
    public SharedFile[] files { get; set; }
}

public sealed class SharedFile
{
    internal const int MaxFileSize = 20 * 1024 * 1024;
    public string id { get; set; }
    public string name { get; set; }
    public int size { get; set; }

    internal static bool IsValid(SharedFile file)
    {
        if (file == null || file.id == null || !Regex.IsMatch(file.id, @"\A[a-f0-9]{32}\z")
            || String.IsNullOrWhiteSpace(file.name) || file.name.Length > 255
            || file.name == "." || file.name == ".." || file.name.EndsWith(".") || file.name.EndsWith(" ")
            || file.name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        return file.size >= 0 && file.size <= MaxFileSize;
    }

    public override string ToString()
    {
        return name + " (" + Math.Ceiling(size / 1024.0) + " KB)";
    }
}

internal sealed class SessionEndedException : Exception { }

internal sealed class FileTransferException : Exception
{
    public FileTransferException(string message) : base(message) { }
}

internal sealed class Session : IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient fileClient;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private string hostToken;
    private bool closing;
    private bool disposing;

    public CancellationToken Token { get; private set; }

    public Session(Uri server)
    {
        Token = lifetime.Token;
        client = MakeClient(server, 3, 128 * 1024);
        fileClient = MakeClient(server, 120, SharedFile.MaxFileSize);
    }

    private static HttpClient MakeClient(Uri server, int seconds, int bufferSize)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = !server.IsLoopback,
            AutomaticDecompression = DecompressionMethods.None
        };
        return new HttpClient(handler)
        {
            BaseAddress = server,
            Timeout = TimeSpan.FromSeconds(seconds),
            MaxResponseContentBufferSize = bufferSize
        };
    }

    public async Task<SessionCode> Create()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        SessionCode data = await ReadJson<SessionCode>(HttpMethod.Post, "/api/session", content);
        if (data.code == null || !Regex.IsMatch(data.code, @"\A[0-9]{8}\z")
            || data.hostToken == null || !Regex.IsMatch(data.hostToken, @"\A[A-Za-z0-9_\-]{24,128}\z"))
            throw new InvalidDataException();
        hostToken = data.hostToken;
        return data;
    }

    public Task<HostState> GetState()
    {
        return ReadJson<HostState>(HttpMethod.Get, "/api/host/state", null);
    }

    public Task<InputBatch> GetInput()
    {
        return ReadJson<InputBatch>(HttpMethod.Get, "/api/host/input", null);
    }

    public Task<HostState> SendMonitors(DisplayMonitor[] monitors)
    {
        string body = new JavaScriptSerializer().Serialize(new { monitors });
        return ReadJson<HostState>(HttpMethod.Post, "/api/host/monitors", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    public async Task SendFrame(byte[] frame, int monitorRevision)
    {
        var content = new ByteArrayContent(frame);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using (await Request(client, HttpMethod.Post, "/api/host/frame", content, Token, monitorRevision: monitorRevision)) { }
    }

    public async Task ClearFrame()
    {
        using (await Request(client, HttpMethod.Delete, "/api/host/frame", null, Token)) { }
    }

    public Task<Inbox> ListFiles()
    {
        return ReadJson<Inbox>(HttpMethod.Get, "/api/host/files", null);
    }

    public async Task ReportFile(FileResponse result)
    {
        var body = new JavaScriptSerializer().Serialize(result);
        using (await Request(client, HttpMethod.Post, "/api/host/browse", new StringContent(body, Encoding.UTF8, "application/json"), Token)) { }
    }

    public async Task Upload(string name, Stream file)
    {
        if (file.Length > SharedFile.MaxFileSize)
            throw new FileTransferException("20 MB 이하인 파일을 선택하세요.");
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = file.Length;
        content.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        using (await Request(fileClient, HttpMethod.Post, "/api/host/files", content, Token, true)) { }
    }

    public async Task<byte[]> Download(SharedFile file)
    {
        using (HttpResponseMessage response = await Request(fileClient, HttpMethod.Get, "/api/host/files/" + file.id, null, Token, true))
        {
            if (response.Content.Headers.ContentLength != file.size)
                throw new InvalidDataException();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length != file.size)
                throw new InvalidDataException();
            return bytes;
        }
    }

    public async Task Dismiss(string id)
    {
        using (await Request(client, HttpMethod.Delete, "/api/host/files/" + id, null, Token, true)) { }
    }

    public async Task End()
    {
        if (hostToken == null)
            return;
        closing = true;
        try
        {
            var content = new StringContent("{\"action\":\"end\"}", Encoding.UTF8, "application/json");
            using (await Request(client, HttpMethod.Post, "/api/host/action", content, CancellationToken.None)) { }
        }
        finally
        {
            closing = false;
            if (disposing)
                Dispose();
        }
    }

    private async Task<T> ReadJson<T>(HttpMethod method, string path, HttpContent content) where T : class
    {
        using (HttpResponseMessage response = await Request(client, method, path, content, Token))
        {
            string body = await response.Content.ReadAsStringAsync();
            T data = new JavaScriptSerializer().Deserialize<T>(body);
            if (data == null)
                throw new InvalidDataException();
            return data;
        }
    }

    private async Task<HttpResponseMessage> Request(HttpClient connection, HttpMethod method, string path,
        HttpContent content, CancellationToken token, bool file = false, int monitorRevision = 0)
    {
        using (var request = new HttpRequestMessage(method, path))
        {
            request.Content = content;
            if (path == "/api/session")
            {
                request.Headers.Add("X-DLI-File-Browser", "2");
                request.Headers.Add("X-DLI-Monitor-Switch", "1");
            }
            if (monitorRevision > 0)
                request.Headers.Add("X-DLI-Monitor-Revision", monitorRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (hostToken != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostToken);
            HttpResponseMessage response = await connection.SendAsync(request, token);
            if (response.IsSuccessStatusCode)
                return response;

            HttpStatusCode code = response.StatusCode;
            response.Dispose();
            if (file && code == HttpStatusCode.NotFound)
                throw new FileTransferException("파일이 이미 저장되었거나 수신 목록에서 사라졌습니다.");
            if (file && code == HttpStatusCode.RequestEntityTooLarge)
                throw new FileTransferException("20 MB 이하인 파일을 선택하세요.");
            if (file && code == HttpStatusCode.Conflict)
                throw new FileTransferException("상담원의 수신 목록이 가득 찼습니다. 잠시 후 다시 보내세요.");
            if (file && (int)code == 429)
                throw new FileTransferException("서버의 전송 공간이 가득 찼습니다. 잠시 후 다시 보내세요.");
            if (code == HttpStatusCode.Unauthorized || code == HttpStatusCode.NotFound || code == HttpStatusCode.Gone)
                throw new SessionEndedException();
            throw new HttpRequestException();
        }
    }

    public void Cancel()
    {
        lifetime.Cancel();
    }

    public void Dispose()
    {
        if (closing)
        {
            disposing = true;
            return;
        }
        client.Dispose();
        fileClient.Dispose();
        lifetime.Dispose();
    }
}

internal static class ScreenCapture
{
    private const int MaxFrameBytes = 1536 * 1024;
    private static readonly ImageCodecInfo Jpeg = Array.Find(ImageCodecInfo.GetImageEncoders(),
        codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    public static Rectangle PrimaryArea()
    {
        Screen screen = Screen.PrimaryScreen;
        return screen == null ? Rectangle.Empty : screen.Bounds;
    }

    public static Rectangle GetArea(string id)
    {
        Screen screen = Array.Find(Screen.AllScreens, item => item.DeviceName == id);
        return screen == null ? Rectangle.Empty : screen.Bounds;
    }

    public static DisplayMonitor[] GetMonitors()
    {
        Screen[] screens = (Screen[])Screen.AllScreens.Clone();
        Array.Sort(screens, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.DeviceName, right.DeviceName));
        return Array.ConvertAll(screens, screen => new DisplayMonitor {
            id = screen.DeviceName, width = screen.Bounds.Width, height = screen.Bounds.Height,
            primary = screen.Primary, Bounds = screen.Bounds
        });
    }

    public static bool SameMonitors(DisplayMonitor[] first, DisplayMonitor[] second)
    {
        if (first == null || first.Length != second.Length)
            return false;
        for (int index = 0; index < first.Length; index++)
        {
            if (first[index].id != second[index].id || first[index].Bounds != second[index].Bounds
                || first[index].primary != second[index].primary)
                return false;
        }
        return true;
    }

    public static byte[] Capture(Rectangle area)
    {
        using (var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(full))
                graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);

            double scale = Math.Min(1.0, Math.Min(1600.0 / area.Width, 1100.0 / area.Height));
            if (scale == 1.0)
                return Encode(full);
            int width = Math.Max(1, (int)Math.Round(area.Width * scale));
            int height = Math.Max(1, (int)Math.Round(area.Height * scale));
            using (var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.DrawImage(full, 0, 0, width, height);
                }
                return Encode(resized);
            }
        }
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        foreach (long quality in new long[] { 68L, 40L, 20L })
        {
            using (var bytes = new MemoryStream())
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bitmap.Save(bytes, Jpeg, parameters);
                if (bytes.Length <= MaxFrameBytes)
                    return bytes.ToArray();
            }
        }
        throw new InvalidDataException();
    }
}

internal sealed class NativeInput
{
    private const uint MouseType = 0;
    private const uint KeyboardType = 1;
    private const uint Move = 0x0001;
    private const uint Wheel = 0x0800;
    private const uint Absolute = 0x8000;
    private const uint VirtualDesktop = 0x4000;
    private const uint PositionedMouse = Move | Absolute | VirtualDesktop;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private static readonly uint[] DownFlags = { 0x0002, 0x0020, 0x0008 };
    private static readonly uint[] UpFlags = { 0x0004, 0x0040, 0x0010 };
    private readonly HashSet<int> heldKeys = new HashSet<int>();
    private readonly HashSet<int> heldButtons = new HashSet<int>();

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, uint length, out uint needed);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint type;
        internal InputUnion data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput mouse;
        [FieldOffset(0)]
        internal KeyboardInput keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int x;
        internal int y;
        internal uint data;
        internal uint flags;
        internal uint time;
        internal UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort key;
        internal ushort scan;
        internal uint flags;
        internal uint time;
        internal UIntPtr extraInfo;
    }

    internal static bool IsInputDesktop()
    {
        IntPtr input = OpenInputDesktop(0, false, 0x0001);
        if (input == IntPtr.Zero)
            return false;
        try
        {
            string activeName = DesktopName(input);
            return activeName != null && activeName == DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        }
        finally { CloseDesktop(input); }
    }

    private static string DesktopName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        uint needed;
        return GetUserObjectInformation(handle, 2, name, (uint)(name.Capacity * 2), out needed) ? name.ToString() : null;
    }

    public void Apply(RemoteEvent input, Rectangle area, Rectangle protectedPanel, bool panelFocused, Rectangle filePanel)
    {
        if (input == null)
            return;
        if (input.type == "reset")
        {
            ReleaseAll();
            return;
        }
        if (input.type == "text")
        {
            if (!panelFocused)
                EmitText(input.text);
            return;
        }
        if (input.type == "keydown" || input.type == "keyup")
        {
            if (input.key.HasValue && input.key.Value >= 8 && input.key.Value <= 254)
                ApplyKey(input.key.Value, input.extended, input.type == "keyup", panelFocused);
            return;
        }
        ApplyMouse(input, area, protectedPanel, filePanel);
    }

    private void ApplyKey(int key, bool extended, bool up, bool panelFocused)
    {
        int identity = key | (extended ? 0x100 : 0);
        if (up)
        {
            if (heldKeys.Contains(identity) && EmitKey(identity, true))
                heldKeys.Remove(identity);
        }
        else if (!panelFocused && EmitKey(identity, false))
        {
            heldKeys.Add(identity);
        }
    }

    private void ApplyMouse(RemoteEvent input, Rectangle area, Rectangle protectedPanel, Rectangle filePanel)
    {
        int button = input.button.GetValueOrDefault(-1);
        bool validButton = button >= 0 && button < DownFlags.Length;
        bool usable = !area.IsEmpty && IsCoordinate(input.x) && IsCoordinate(input.y);
        int x = 0, y = 0;
        if (usable)
        {
            int pixelX = area.Left + Math.Min(area.Width - 1, (int)(input.x.Value * area.Width));
            int pixelY = area.Top + Math.Min(area.Height - 1, (int)(input.y.Value * area.Height));
            Rectangle desktop = SystemInformation.VirtualScreen;
            usable = desktop.Contains(pixelX, pixelY) && !Rectangle.Inflate(protectedPanel, 3, 3).Contains(pixelX, pixelY)
                && (filePanel.IsEmpty || !Rectangle.Inflate(filePanel, 3, 3).Contains(pixelX, pixelY));
            if (usable)
            {
                x = AbsoluteCoordinate(pixelX, desktop.Left, desktop.Width);
                y = AbsoluteCoordinate(pixelY, desktop.Top, desktop.Height);
            }
        }

        if (input.type == "up" && validButton && heldButtons.Contains(button))
        {
            uint position = usable ? PositionedMouse : 0;
            if (EmitMouse(x, y, 0, position | UpFlags[button]))
                heldButtons.Remove(button);
            return;
        }
        if (!usable)
            return;
        if (input.type == "move")
            EmitMouse(x, y, 0, PositionedMouse);
        if (input.type == "down" && validButton && !heldButtons.Contains(button)
            && EmitMouse(x, y, 0, PositionedMouse | DownFlags[button]))
            heldButtons.Add(button);
        if (input.type == "wheel" && input.delta.HasValue && !Double.IsNaN(input.delta.Value) && input.delta.Value != 0)
        {
            int ticks = Math.Sign(input.delta.Value);
            EmitMouse(x, y, unchecked((uint)(-ticks * 120)), PositionedMouse | Wheel);
        }
    }

    private static int AbsoluteCoordinate(int pixel, int origin, int length)
    {
        return (int)(((long)(pixel - origin) * 65536 + 32768) / length);
    }

    private static bool IsCoordinate(double? value)
    {
        return value.HasValue && value.Value >= 0 && value.Value <= 1;
    }

    public void ReleaseAll()
    {
        foreach (int key in new List<int>(heldKeys))
        {
            if (EmitKey(key, true))
                heldKeys.Remove(key);
        }
        foreach (int button in new List<int>(heldButtons))
        {
            if (EmitMouse(0, 0, 0, UpFlags[button]))
                heldButtons.Remove(button);
        }
    }

    private static bool EmitKey(int identity, bool up)
    {
        var input = new Input { type = KeyboardType };
        input.data.keyboard = new KeyboardInput
        {
            key = (ushort)(identity & 0xff),
            flags = ((identity & 0x100) != 0 ? KeyExtended : 0) | (up ? KeyUp : 0)
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) == 1;
    }

    private static void EmitText(string text)
    {
        if (String.IsNullOrEmpty(text) || text.Length > 256)
            return;
        var inputs = new Input[text.Length * 2];
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (Char.IsControl(character))
                return;
            inputs[index * 2] = UnicodeInput(character, false);
            inputs[index * 2 + 1] = UnicodeInput(character, true);
        }
        uint count = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        if ((count & 1) != 0)
        {
            Input release = inputs[count];
            SendInput(1, new[] { release }, Marshal.SizeOf(typeof(Input)));
        }
    }

    private static Input UnicodeInput(char character, bool up)
    {
        var input = new Input { type = KeyboardType };
        input.data.keyboard = new KeyboardInput { scan = character, flags = KeyUnicode | (up ? KeyUp : 0) };
        return input;
    }

    private static bool EmitMouse(int x, int y, uint data, uint flags)
    {
        var input = new Input { type = MouseType };
        input.data.mouse = new MouseInput { x = x, y = y, data = data, flags = flags };
        return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) == 1;
    }
}
