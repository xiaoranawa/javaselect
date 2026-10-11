using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace JavaSelect.App.Services;

internal readonly record struct NotificationShortcutIdentity(string AppId, Guid Activator);
internal sealed record NotificationShortcutInfo(string Target, string Arguments, string Description, string? AppId, Guid? Activator);

internal static class NotificationShortcut
{
    internal const string OwnedDescription = "JavaSelect notification shortcut (managed)";
    private const string AppIdsKey = @"Software\Classes\AppUserModelId\";

    internal static void EnsureRegistered(string executablePath)
    {
        var executable = Path.GetFullPath(executablePath);
        var identity = ReadIdentity(executable) ?? throw new IOException("Windows notification registration is incomplete. Restart or reinstall JavaSelect.");
        Ensure(executable, Environment.GetFolderPath(Environment.SpecialFolder.Programs), identity);
    }

    internal static void RemoveOwned(string executablePath)
    {
        var executable = Path.GetFullPath(executablePath);
        var identity = ReadIdentity(executable);
        if (identity is { } value) RemoveOwned(executable, Environment.GetFolderPath(Environment.SpecialFolder.Programs), value);
    }

    private static NotificationShortcutIdentity? ReadIdentity(string executable)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var mapping = user.OpenSubKey(AppIdsKey + executable.Replace('\\', '.'));
        if (!Guid.TryParse(mapping?.GetValue("NotificationGUID") as string, out var appId)) return null;
        var id = appId.ToString("B").ToUpperInvariant();
        using var assets = user.OpenSubKey(AppIdsKey + id);
        if (!Guid.TryParse(assets?.GetValue("CustomActivator") as string, out var activator)) return null;
        using var server = user.OpenSubKey(@"Software\Classes\CLSID\" + activator.ToString("B") + @"\LocalServer32");
        if (server?.GetValue(null) is not string command || !MatchesServer(command, executable)) return null;
        return new(id, activator);
    }

    internal static bool MatchesServer(string command, string executable)
    {
        var arguments = CommandLineToArgvW(command, out var count);
        if (arguments == 0) return false;
        try
        {
            if (count != 2) return false;
            var path = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments));
            var activation = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, IntPtr.Size));
            return path is not null && Path.IsPathFullyQualified(path) && SamePath(path, executable)
                && activation == "----AppNotificationActivated:";
        }
        finally { LocalFree(arguments); }
    }

    internal static string Ensure(string executablePath, string programsDirectory, NotificationShortcutIdentity identity)
    {
        var executable = Path.GetFullPath(executablePath);
        if (!File.Exists(executable)) throw new FileNotFoundException("JavaSelect could not locate its executable for notifications.", executable);
        var paths = ShortcutPaths(executable, programsDirectory);
        foreach (var path in paths)
        {
            var current = TryRead(path);
            if (current is null || !MatchesTarget(current, executable)) continue;
            if (current.AppId == identity.AppId && current.Activator == identity.Activator) return path;
            AddIdentity(path, identity);
            return path;
        }
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path)) continue;
            SaveNew(path, executable, "", OwnedDescription, identity);
            return path;
        }
        throw new IOException("The JavaSelect notification shortcut is occupied by another application. Rename that shortcut and restart JavaSelect.");
    }

    internal static void RemoveOwned(string executablePath, string programsDirectory, NotificationShortcutIdentity identity)
    {
        var executable = Path.GetFullPath(executablePath);
        foreach (var path in ShortcutPaths(executable, programsDirectory))
        {
            var info = TryRead(path);
            if (info is not null && info.Description == OwnedDescription && MatchesTarget(info, executable)
                && info.AppId == identity.AppId && info.Activator == identity.Activator) File.Delete(path);
        }
    }

    private static string[] ShortcutPaths(string executable, string programsDirectory)
    {
        if (!Path.IsPathFullyQualified(programsDirectory)) throw new IOException("The current user's Start menu is unavailable. Sign in again and retry.");
        var folder = Path.Combine(Path.GetFullPath(programsDirectory), "JavaSelect");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())))[..12].ToLowerInvariant();
        return [Path.Combine(folder, "JavaSelect.lnk"), Path.Combine(folder, $"JavaSelect ({suffix}).lnk")];
    }
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static bool MatchesTarget(NotificationShortcutInfo link, string executable) => string.IsNullOrEmpty(link.Arguments)
        && Path.IsPathFullyQualified(link.Target) && SamePath(link.Target, executable);
    private static NotificationShortcutInfo? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Read(path); }
        catch (Exception error) when (error is IOException or COMException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static NotificationShortcutInfo Read(string path)
    {
        object value = new ShellLink();
        try
        {
            ((IPersistFile)value).Load(path, 0);
            var link = (IShellLinkW)value;
            var target = new StringBuilder(32768); var arguments = new StringBuilder(32768); var description = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, 0, 0); link.GetArguments(arguments, arguments.Capacity); link.GetDescription(description, description.Capacity);
            var store = (IPropertyStore)value; var idKey = new PropertyKey(5); var activatorKey = new PropertyKey(26);
            PropVariant appId = default, activator = default;
            try
            {
                store.GetValue(ref idKey, out appId); store.GetValue(ref activatorKey, out activator);
                return new(target.ToString(), arguments.ToString(), description.ToString(),
                    appId.Type == 31 ? Marshal.PtrToStringUni(appId.Value) : null,
                    activator.Type == 72 ? Marshal.PtrToStructure<Guid>(activator.Value) : null);
            }
            finally { PropVariantClear(ref appId); PropVariantClear(ref activator); }
        }
        finally { Marshal.FinalReleaseComObject(value); }
    }

    internal static void SaveNew(string path, string executable, string arguments, string description, NotificationShortcutIdentity? identity = null)
    {
        object value = new ShellLink();
        try
        {
            var link = (IShellLinkW)value;
            link.SetPath(executable); link.SetArguments(arguments); link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription(description); link.SetIconLocation(executable, 0); link.SetShowCmd(1);
            if (identity is { } registration) SetIdentity((IPropertyStore)value, registration);
            Save((IPersistFile)value, path, null);
        }
        finally { Marshal.FinalReleaseComObject(value); }
    }
    private static void AddIdentity(string path, NotificationShortcutIdentity identity)
    {
        var original = File.ReadAllBytes(path);
        object value = new ShellLink();
        try
        {
            ((IPersistFile)value).Load(path, 2);
            SetIdentity((IPropertyStore)value, identity);
            Save((IPersistFile)value, path, original);
        }
        finally { Marshal.FinalReleaseComObject(value); }
    }
    private static void SetIdentity(IPropertyStore store, NotificationShortcutIdentity identity)
    {
        var idKey = new PropertyKey(5); var activatorKey = new PropertyKey(26);
        var appId = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(identity.AppId) };
        var activator = new PropVariant { Type = 72, Value = Marshal.AllocCoTaskMem(16) };
        try
        {
            Marshal.StructureToPtr(identity.Activator, activator.Value, false);
            store.SetValue(ref idKey, ref appId); store.SetValue(ref activatorKey, ref activator); store.Commit();
        }
        finally { PropVariantClear(ref appId); PropVariantClear(ref activator); }
    }
    private static void Save(IPersistFile file, string path, byte[]? original)
    {
        var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".javaselect-shortcut-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            file.Save(temporary, true);
            if (original is not null && (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(original)))
                throw new IOException("The JavaSelect shortcut changed while its notification settings were being saved. Retry after closing its Properties window.");
            File.Move(temporary, path, overwrite: original is not null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint data, uint flags);
        void GetIDList(out nint value); void SetIDList(nint value);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value); void SetHotkey(short value); void GetShowCmd(out int value); void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved); void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(uint id) { public Guid Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Value; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value); void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
