using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

internal static class Bootstrap
{
    private const string ElevatedFlag = "--javaselect-elevated";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string sid;
            bool elevated;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                sid = identity.User.Value;
                elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            var arguments = RawArguments(Environment.CommandLine);
            var workingDirectory = ValidateWorkingDirectory(Environment.CurrentDirectory);

            if (args.Length > 0 && args[0] == ElevatedFlag)
            {
                if (!elevated || args.Length != 4 || !string.Equals(args[1], sid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Text(
                        "请使用当前 Windows 账户的管理员权限安装。使用其他账户授权会更改安装归属，安装已取消。",
                        "請使用目前 Windows 帳戶的系統管理員權限安裝。使用其他帳戶授權會變更安裝歸屬，安裝已取消。",
                        "Install using administrator privileges for your current Windows account. Authorization with another account would change the installation owner, so installation was canceled.",
                        "現在の Windows アカウントの管理者権限でインストールしてください。別のアカウントで承認するとインストール先のユーザーが変わるため、中止しました。"));
                arguments = DecodeArgument(args[2]);
                workingDirectory = ValidateWorkingDirectory(DecodeArgument(args[3]));
            }
            else if (!elevated)
            {
                return Elevate(sid, arguments, workingDirectory);
            }

            return RunInstaller(arguments, workingDirectory);
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode == 1223) return 2;
            ShowError(error);
            return 1;
        }
        catch (Exception error)
        {
            ShowError(error);
            return 1;
        }
    }

    private static int Elevate(string sid, string arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, ElevationArguments(sid, arguments, workingDirectory))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDirectory
        };
        using (var process = Process.Start(start))
        {
            if (process == null) throw new InvalidOperationException(StartFailure());
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static int RunInstaller(string arguments, string workingDirectory)
    {
        string directory = null;
        try
        {
            directory = CreateProtectedDirectory();
            EnsureNoReparsePoints(directory);
            var executable = Path.Combine(directory, "setup.exe");
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("JavaSelect.SetupPayload"))
            {
                if (payload == null) throw new InvalidDataException(PayloadFailure());
                using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    long written = 0;
                    int count;
                    while ((count = payload.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        written = checked(written + count);
                        if (written > BootstrapPayloadInfo.Length) throw new InvalidDataException(PayloadFailure());
                        output.Write(buffer, 0, count);
                    }
                    if (written != BootstrapPayloadInfo.Length) throw new InvalidDataException(PayloadFailure());
                    output.Flush(true);
                }
            }

            EnsureNoReparsePoints(executable);
            using (var verified = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!PayloadMatches(verified, BootstrapPayloadInfo.Length, BootstrapPayloadInfo.Sha256))
                    throw new InvalidDataException(PayloadFailure());
                var start = new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = workingDirectory
                };
                start.EnvironmentVariables["TEMP"] = directory;
                start.EnvironmentVariables["TMP"] = directory;
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException(StartFailure());
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
        }
        finally
        {
            if (directory != null)
            {
                try { RemoveOwnedDirectory(directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }
    }

    private static string CreateProtectedDirectory()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        EnsureNoReparsePoints(parent);
        var directory = Path.Combine(parent, "JavaSelect-Setup-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var account in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, pointer, descriptor.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pointer };
            if (!CreateDirectory(directory, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return directory;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Text("安装临时路径不能包含符号链接或目录联接。", "安裝暫存路徑不能包含符號連結或目錄連接。",
                    "The installation temporary path must not contain symbolic links or directory junctions.", "インストールの一時パスにシンボリックリンクやジャンクションは使用できません。"));
    }

    private static void RemoveOwnedDirectory(string directory)
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var resolved = Path.GetFullPath(directory);
        var name = Path.GetFileName(resolved);
        Guid suffix;
        if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("JavaSelect-Setup-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name.Substring("JavaSelect-Setup-".Length), "N", out suffix))
            throw new IOException("Unexpected installation temporary directory.");
        RemoveDirectoryTree(resolved);
    }

    private static void RemoveDirectoryTree(string directory)
    {
        EnsureNoReparsePoints(directory);
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) RemoveDirectoryTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory, false);
    }

    internal static bool PayloadMatches(Stream payload, long expectedLength, string expectedHash)
    {
        if (expectedLength <= 0 || expectedHash == null || expectedHash.Length != 64 || !payload.CanSeek || payload.Length != expectedLength) return false;
        payload.Position = 0;
        using (var sha256 = SHA256.Create())
        {
            var hash = BitConverter.ToString(sha256.ComputeHash(payload)).Replace("-", "");
            payload.Position = 0;
            return string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string RawArguments(string commandLine)
    {
        if (commandLine == null) throw new ArgumentNullException("commandLine");
        var quoted = false;
        var index = 0;
        while (index < commandLine.Length)
        {
            var value = commandLine[index];
            if (value == '"') quoted = !quoted;
            else if (!quoted && value <= ' ') break;
            index++;
        }
        while (index < commandLine.Length && commandLine[index] <= ' ') index++;
        return commandLine.Substring(index);
    }

    internal static string ElevationArguments(string sid, string arguments, string workingDirectory)
    {
        new SecurityIdentifier(sid);
        return ElevatedFlag + " " + sid + " \"" + EncodeArgument(arguments) + "\" \"" + EncodeArgument(workingDirectory) + "\"";
    }

    internal static string EncodeArgument(string value)
    {
        return Convert.ToBase64String(new UTF8Encoding(false, true).GetBytes(value));
    }

    internal static string DecodeArgument(string value)
    {
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
    }

    internal static string ValidateWorkingDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory)) throw new IOException("The installation working directory must be an absolute path.");
        var fullPath = Path.GetFullPath(directory);
        if (!string.Equals(directory, fullPath, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fullPath))
            throw new IOException("The installation working directory is not available.");
        return fullPath;
    }

    private static string StartFailure()
    {
        return Text("无法启动安装程序。", "無法啟動安裝程式。", "The installer could not be started.", "インストーラーを起動できませんでした。");
    }

    private static string PayloadFailure()
    {
        return Text("安装文件校验失败，请重新下载安装包。", "安裝檔案驗證失敗，請重新下載安裝套件。",
            "Installer verification failed. Download the installer again.", "インストールファイルの検証に失敗しました。インストーラーを再ダウンロードしてください。");
    }

    private static void ShowError(Exception error)
    {
        MessageBox(IntPtr.Zero, Text("无法启动 JavaSelect 安装程序。", "無法啟動 JavaSelect 安裝程式。",
            "Unable to start the JavaSelect installer.", "JavaSelect インストーラーを起動できませんでした。") + Environment.NewLine + Environment.NewLine + error.Message,
            "JavaSelect", 0x10);
    }

    private static string Text(string simplified, string traditional, string english, string japanese)
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return japanese;
        if (!name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return english;
        return name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ? traditional : simplified;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes security);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string message, string title, uint type);
}
