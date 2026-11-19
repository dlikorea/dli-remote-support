using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Product
{
    public const string Title = "디엘아이 원격지원";
    public const string AppName = "DLI-RemoteSupport.exe";
    public const string SetupName = "DLI-RemoteSupport-Setup.exe";
    public static readonly Version Release = new Version(ReleaseSettings.Version);
    public static readonly string Root = InstallRoot();
    public static readonly string AppPath = Path.Combine(Root, AppName);
    public static readonly string MaintenancePath = Path.Combine(Root, SetupName);
    public static readonly string StagingPath = Path.Combine(Root, "Updates");
    public static readonly Uri DefaultServer = new Uri(ReleaseSettings.Server);
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DLI.RemoteSupport";

    private static string InstallRoot()
    {
#if DLI_TEST
        string testRoot = Environment.GetEnvironmentVariable("DLI_TEST_ROOT");
        if (String.IsNullOrEmpty(testRoot) || !Path.IsPathRooted(testRoot))
            throw new InvalidOperationException("DLI_TEST_ROOT must be an absolute path.");
        return Path.GetFullPath(testRoot);
#else
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLI", "RemoteSupport");
#endif
    }

    public static Mutex ApplicationMutex()
    {
#if DLI_TEST
        string suffix = Root.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
#else
        string suffix = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
#endif
        return new Mutex(false, "Local\\DLI.RemoteSupport." + suffix);
    }

    public static bool Acquire(Mutex mutex, int milliseconds)
    {
        try { return mutex.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; }
    }

    public static bool IsOperator(string[] args)
    {
        return Array.IndexOf(args, "--operator") >= 0;
    }

    public static void Start(string path, string args)
    {
        using (Process.Start(new ProcessStartInfo(path, args) {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) })) { }
    }

    public static string Self
    {
        get { return Assembly.GetExecutingAssembly().Location; }
    }

    public static Version InstalledVersion()
    {
        if (!File.Exists(AppPath)) return null;
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(AppPath);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    public static void Register(bool operatorShortcut)
    {
#if !DLI_TEST
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", Title);
            key.SetValue("DisplayVersion", Release.ToString());
            key.SetValue("Publisher", "디엘아이");
            key.SetValue("InstallLocation", Root);
            key.SetValue("DisplayIcon", AppPath);
            key.SetValue("UninstallString", "\"" + MaintenancePath + "\" --uninstall");
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
        }
        WriteShortcut(ShortcutPath(false), "");
        if (operatorShortcut) WriteShortcut(ShortcutPath(true), "--operator");
#endif
    }

    private static void WriteShortcut(string shortcut, string arguments)
    {
        Type shell = Type.GetTypeFromProgID("WScript.Shell", true);
        object instance = Activator.CreateInstance(shell);
        object link = null;
        try
        {
            link = shell.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, instance, new object[] { shortcut });
            Type type = link.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { AppPath });
            type.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Root });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { Title });
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { AppPath + ",0" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
        }
    }

    public static void Unregister()
    {
#if !DLI_TEST
        File.Delete(ShortcutPath(false));
        File.Delete(ShortcutPath(true));
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
#endif
    }

    private static string ShortcutPath(bool operatorShortcut)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Title + (operatorShortcut ? " 상담원" : "") + ".lnk");
    }

    public static void InitUi()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    public static void CleanStaging()
    {
        if (!Directory.Exists(StagingPath)) return;
        foreach (string path in Directory.GetFiles(StagingPath, "setup-*.exe"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-1)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
