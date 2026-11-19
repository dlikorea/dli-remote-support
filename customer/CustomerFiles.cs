using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class FileRequest
{
    public string id { get; set; }
    public string action { get; set; }
    public string root { get; set; }
    public string[] segments { get; set; }
}

public sealed class FileResponse
{
    public string id { get; set; }
    public CustomerFileEntry[] entries { get; set; }
    public string[] drives { get; set; }
    public bool truncated { get; set; }
    public string error { get; set; }
}

public sealed class CustomerFileEntry
{
    public string name { get; set; }
    public bool folder { get; set; }
    public long size { get; set; }
}

internal static class CustomerFiles
{
    private const int EntryLimit = 500;
    private const FileAttributes Excluded = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        | FileAttributes.Offline | FileAttributes.Device;

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int length);

    private static bool IsDrive(string root)
    {
        return root != null && root.Length == 2 && root[0] >= 'A' && root[0] <= 'Z' && root[1] == ':';
    }

    private static bool IsAvailable(DriveInfo drive)
    {
        switch (drive.DriveType)
        {
            case DriveType.Network: return true;
            case DriveType.Fixed:
            case DriveType.Removable:
            case DriveType.CDRom:
            case DriveType.Ram: return drive.IsReady;
            default: return false;
        }
    }

    public static FileResponse ListDrives(FileRequest request, CancellationToken token)
    {
        var drives = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            string letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
            if (IsDrive(letter) && IsAvailable(drive)) drives.Add(letter);
        }
        drives.Sort(StringComparer.Ordinal);
        return new FileResponse { id = request.id, drives = drives.ToArray() };
    }

    private static string ResolveRoot(string folder)
    {
        if (IsDrive(folder))
        {
            string driveRoot = folder + Path.DirectorySeparatorChar;
            if (!IsAvailable(new DriveInfo(driveRoot))) throw new IOException();
            return driveRoot;
        }
        switch (folder)
        {
            case "desktop": return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            case "documents": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            case "downloads":
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                IntPtr pointer;
                int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer);
                try { return result == 0 ? Marshal.PtrToStringUni(pointer) : null; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            default: return null;
        }
    }

    private static bool ValidName(string name)
    {
        return !String.IsNullOrWhiteSpace(name) && name.Length <= 255 && name != "." && name != ".."
            && !name.EndsWith(" ", StringComparison.Ordinal) && !name.EndsWith(".", StringComparison.Ordinal)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static FileAttributes Inspect(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & Excluded) != 0) throw new IOException();
        return attributes;
    }

    private static string Resolve(FileRequest request, bool folder)
    {
        string root = ResolveRoot(request.root);
        bool drive = IsDrive(request.root);
        string[] parts = request.segments;
        if (String.IsNullOrEmpty(root) || !Path.IsPathRooted(root) || root.StartsWith(@"\\", StringComparison.Ordinal)
            || parts == null || parts.Length > (drive ? 64 : 16) || (!folder && parts.Length == 0)) throw new IOException();
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Windows marks volume roots Hidden|System; those flags must still exclude their children.
        FileAttributes rootAttributes = File.GetAttributes(root);
        FileAttributes excludedRoot = drive ? Excluded & ~(FileAttributes.Hidden | FileAttributes.System) : Excluded;
        if ((!drive && root.Length <= 3) || (rootAttributes & excludedRoot) != 0
            || (rootAttributes & FileAttributes.Directory) == 0) throw new IOException();
        string current = root;
        for (int index = 0; index < parts.Length; index++)
        {
            if (!ValidName(parts[index])) throw new IOException();
            current = Path.GetFullPath(Path.Combine(current, parts[index]));
            if (!current.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            bool isDirectory = (Inspect(current) & FileAttributes.Directory) != 0;
            if (isDirectory != (folder || index < parts.Length - 1)) throw new IOException();
        }
        return current;
    }

    public static FileResponse List(FileRequest request, CancellationToken token)
    {
        string folder = Resolve(request, true);
        var entries = new List<CustomerFileEntry>();
        bool truncated = false;
        foreach (string path in Directory.EnumerateFileSystemEntries(folder))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!ValidName(name)) continue;
            try
            {
                if (entries.Count >= EntryLimit) { truncated = true; break; }
                bool directory = (Inspect(path) & FileAttributes.Directory) != 0;
                entries.Add(new CustomerFileEntry { name = name, folder = directory, size = directory ? 0 : new FileInfo(path).Length });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new FileResponse { id = request.id, entries = entries.OrderByDescending(entry => entry.folder)
            .ThenBy(entry => entry.name, StringComparer.CurrentCultureIgnoreCase).ToArray(), truncated = truncated };
    }

    public static FileStream Open(FileRequest request)
    {
        string path = Resolve(request, false);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        try
        {
            if (file.Length > SharedFile.MaxFileSize) throw new FileTransferException("20 MB 이하인 파일만 가져올 수 있습니다.");
            var finalPath = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(file.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
            if (length == 0 || length >= finalPath.Capacity || !MatchesFile(path, finalPath.ToString())) throw new IOException();
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static bool MatchesFile(string path, string resolved)
    {
        if (String.Equals(resolved, @"\\?\" + path, StringComparison.OrdinalIgnoreCase)) return true;
        string letter = path.Substring(0, 2);
        if (!resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            var device = new StringBuilder(32768);
            if (QueryDosDevice(letter, device, device.Capacity) == 0) return false;
            string target = device.ToString().Split('\0')[0];
            if (!target.StartsWith(@"\??\", StringComparison.Ordinal) || target.Length < 7 || !IsDrive(target.Substring(4, 2))) return false;
            string translated = Path.Combine(target.Substring(4), path.Substring(3));
            return String.Equals(resolved, @"\\?\" + translated, StringComparison.OrdinalIgnoreCase);
        }
        var share = new StringBuilder(32768);
        int length = share.Capacity;
        if (WNetGetConnection(letter, share, ref length) != 0) return false;
        string mapped = share.ToString().TrimEnd('\\') + path.Substring(2);
        return mapped.StartsWith(@"\\", StringComparison.Ordinal)
            && String.Equals(resolved, @"\\?\UNC\" + mapped.Substring(2), StringComparison.OrdinalIgnoreCase);
    }
}
