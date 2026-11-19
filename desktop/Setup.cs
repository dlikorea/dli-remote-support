using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class Setup
{
    [STAThread]
    private static int Main(string[] args)
    {
        Product.InitUi();
        bool quiet = Array.IndexOf(args, "--quiet") >= 0;
        bool update = args.Length >= 2 && args[0] == "--apply-update";
        bool operatorMode = Product.IsOperator(args);
        try
        {
            if (args.Length == 2 && args[0] == "--uninstall-worker")
            {
                Remove(Int32.Parse(args[1]));
                return 0;
            }
            if (args.Length == 1 && args[0] == "--uninstall")
            {
                if (MessageBox.Show("디엘아이 원격지원을 제거할까요?", Product.Title, MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) == DialogResult.OK)
                {
                    string worker = Path.Combine(Path.GetTempPath(), "dli-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(Product.Self, worker);
                    Product.Start(worker, "--uninstall-worker " + Process.GetCurrentProcess().Id);
                }
                return 0;
            }

            if (!quiet && !update)
            {
                using (var prompt = new InstallWindow())
                    if (prompt.ShowDialog() != DialogResult.OK) return 0;
            }

            if (update) WaitForExit(Int32.Parse(args[1]));
            using (Mutex mutex = Product.ApplicationMutex())
            {
                if (!Product.Acquire(mutex, update ? 8000 : 100))
                    throw new InvalidOperationException("열려 있는 디엘아이 원격지원을 종료한 뒤 다시 시도하세요.");
                try { Install(operatorMode); }
                finally { mutex.ReleaseMutex(); }
            }

            if (!quiet || update || Array.IndexOf(args, "--launch") >= 0)
                Product.Start(Product.AppPath, operatorMode ? "--operator" : "");
            return 0;
        }
        catch (Exception error)
        {
            if (!quiet || update)
                MessageBox.Show("설치 작업을 완료할 수 없습니다.\n" + error.Message, Product.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (update && File.Exists(Product.AppPath))
            {
                try { Product.Start(Product.AppPath, "--skip-update " + (operatorMode ? "--operator" : "")); }
                catch (Exception) { }
            }
            return 1;
        }
    }

    private static void Install(bool operatorMode)
    {
        Version installed = Product.InstalledVersion();
        if (installed != null && installed > Product.Release) return;
        Directory.CreateDirectory(Product.Root);
        Product.CleanStaging();
        string stamp = Guid.NewGuid().ToString("N");
        string appStage = Path.Combine(Product.Root, "app-" + stamp + ".tmp");
        string setupStage = Path.Combine(Product.Root, "setup-" + stamp + ".tmp");
        var app = new Replacement(appStage, Product.AppPath);
        var setup = new Replacement(setupStage, Product.MaintenancePath);
        bool committed = false;
        try
        {
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("DLI.App"))
            using (FileStream output = new FileStream(appStage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (payload == null) throw new InvalidDataException("설치 파일의 프로그램을 찾을 수 없습니다.");
                payload.CopyTo(output);
            }
            File.Copy(Product.Self, setupStage);
            if (!String.Equals(Product.Self, Product.MaintenancePath, StringComparison.OrdinalIgnoreCase)) setup.Apply();
            app.Apply();
            Product.Register(operatorMode);
            committed = true;
        }
        finally
        {
            try
            {
                app.Finish(committed);
            }
            finally
            {
                setup.Finish(committed);
            }
        }
    }

    private static void Remove(int parentId)
    {
        if (String.Equals(Product.Self, Product.MaintenancePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("제거 프로그램을 시작할 수 없습니다.");
        WaitForExit(parentId);
        using (Mutex mutex = Product.ApplicationMutex())
        {
            if (!Product.Acquire(mutex, 100)) throw new InvalidOperationException("디엘아이 원격지원을 먼저 종료하세요.");
            try
            {
                File.Delete(Product.AppPath);
                File.Delete(Product.MaintenancePath);
                Product.Unregister();
                if (Directory.Exists(Product.StagingPath))
                {
                    foreach (string file in Directory.GetFiles(Product.StagingPath, "setup-*.exe")) File.Delete(file);
                    if (Directory.GetFileSystemEntries(Product.StagingPath).Length == 0) Directory.Delete(Product.StagingPath);
                }
                if (Directory.Exists(Product.Root) && Directory.GetFileSystemEntries(Product.Root).Length == 0)
                    Directory.Delete(Product.Root);
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static void WaitForExit(int parentId)
    {
        try
        {
            using (Process parent = Process.GetProcessById(parentId))
                if (!parent.WaitForExit(8000)) throw new InvalidOperationException("열려 있는 디엘아이 원격지원을 종료하세요.");
        }
        catch (ArgumentException) { }
    }

    private sealed class Replacement
    {
        private readonly string staged;
        private readonly string target;
        private readonly string backup;
        private bool applied;
        private bool hadPrevious;

        public Replacement(string staged, string target)
        {
            this.staged = staged;
            this.target = target;
            backup = staged + ".bak";
        }

        public void Apply()
        {
            hadPrevious = File.Exists(target);
            if (hadPrevious) File.Replace(staged, target, backup);
            else File.Move(staged, target);
            applied = true;
        }

        private void Undo()
        {
            if (!applied) return;
            if (hadPrevious) File.Replace(backup, target, null);
            else File.Delete(target);
        }

        public void Finish(bool committed)
        {
            if (!committed) Undo();
            try { File.Delete(staged); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
