using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

internal static class DesktopApp
{
    [STAThread]
    private static int Main(string[] args)
    {
        NativeInput.SetProcessDPIAware();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 12;
        Product.InitUi();
        bool operatorMode = Product.IsOperator(args);
        bool skipUpdate = Array.IndexOf(args, "--skip-update") >= 0;
        try
        {
            using (Mutex mutex = Product.ApplicationMutex())
            {
                if (!Product.Acquire(mutex, 500))
                {
                    if (operatorMode) OpenOperator();
                    else MessageBox.Show("디엘아이 원격지원이 이미 실행 중입니다.", Product.Title);
                    return 0;
                }
                try
                {
                    if (!skipUpdate)
                    {
                        using (var startup = new StartupWindow(operatorMode))
                        {
                            Application.Run(startup);
                            if (startup.UpdateStarted || startup.UserClosed) return 0;
                        }
                    }
                    if (!operatorMode)
                    {
                        Application.Run(new CustomerWindow(Product.DefaultServer.AbsoluteUri, lockServer: !Product.DefaultServer.IsLoopback));
                        return 0;
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
            OpenOperator();
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show("프로그램을 시작할 수 없습니다.\n" + error.Message, Product.Title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static void OpenOperator()
    {
        using (Process.Start(new ProcessStartInfo(Product.DefaultServer.AbsoluteUri) { UseShellExecute = true })) { }
    }
}

internal sealed class StartupWindow : Form
{
    private readonly CancellationTokenSource cancel = new CancellationTokenSource();
    private readonly Label status;
    private readonly bool operatorMode;
    private bool ready;
    private bool closing;
    public bool UpdateStarted { get; private set; }
    public bool UserClosed { get; private set; }

    public StartupWindow(bool operatorMode)
    {
        this.operatorMode = operatorMode;
        Text = Product.Title;
        Icon = Brand.LoadIcon();
        Font = new Font("Malgun Gothic", 10F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(352, 90);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        status = new Label { Text = "새 버전을 확인하고 있습니다.", Location = new Point(24, 23), Size = new Size(305, 48) };
        Controls.Add(status);
        Shown += CheckUpdates;
        FormClosing += (sender, args) => {
            if (ready) return;
            args.Cancel = true;
            closing = true;
            cancel.Cancel();
            Hide();
        };
    }

    private async void CheckUpdates(object sender, EventArgs args)
    {
        string setup = null;
        bool offered = false;
        try
        {
            using (var client = new UpdateClient(Product.DefaultServer))
            {
                UpdateManifest manifest = await client.Check(cancel.Token);
                if (closing || manifest == null) return;
                offered = true;
                DialogResult answer = MessageBox.Show(this, "새 버전 " + manifest.version + "을 설치할까요?",
                    Product.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                status.Text = "새 버전을 내려받고 확인하고 있습니다.";
                setup = await client.Download(manifest, cancel.Token);
                if (closing) return;
                Product.Start(setup, "--apply-update " + Process.GetCurrentProcess().Id + (operatorMode ? " --operator" : ""));
                UpdateStarted = true;
            }
        }
        catch (Exception)
        {
            if (offered && !closing)
                MessageBox.Show(this, "업데이트를 설치할 수 없어 현재 버전으로 시작합니다.", Product.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!UpdateStarted && setup != null)
            {
                try { System.IO.File.Delete(setup); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            UserClosed = closing;
            ready = true;
            cancel.Dispose();
            Close();
        }
    }
}
