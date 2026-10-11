using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JavaSelect.App.Services;

public sealed class EnvironmentMonitor : IDisposable
{
    private readonly Action changed;
    private readonly TimeSpan debounce;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent fileChanged = new(false);
    private readonly List<RegistryWatch> registry = [];
    private readonly object watcherGate = new();
    private List<FileSystemWatcher> files = [];
    private readonly Thread thread;
    private volatile bool disposed;
    public event Action<Exception>? Error;

    public EnvironmentMonitor(Action changed, TimeSpan? debounce = null, IEnumerable<string>? javaRoots = null)
    {
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
        this.debounce = debounce ?? TimeSpan.FromSeconds(10);
        if (this.debounce < TimeSpan.FromMilliseconds(100) || this.debounce > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(debounce));
        try
        {
            registry.Add(new RegistryWatch(RegistryHive.CurrentUser, @"Environment"));
            registry.Add(new RegistryWatch(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));
            UpdateJavaRoots(javaRoots ?? []);
            thread = new Thread(Run) { IsBackground = true, Name = "JavaSelect environment monitor" };
            thread.Start();
        }
        catch
        {
            foreach (var watcher in registry) watcher.Dispose();
            foreach (var watcher in files) watcher.Dispose();
            fileChanged.Dispose(); stop.Dispose();
            throw;
        }
    }

    public void UpdateJavaRoots(IEnumerable<string> javaRoots)
    {
        var selected = new List<string>();
        foreach (var path in javaRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
        {
            var normalized = Path.TrimEndingDirectorySeparator(path);
            if (!Directory.Exists(normalized) || string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!), StringComparison.OrdinalIgnoreCase)) continue;
            if (selected.Any(parent => normalized.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            selected.Add(normalized);
            if (selected.Count > 64) throw new ArgumentException("At most 64 independent Java roots can be monitored.", nameof(javaRoots));
        }
        lock (watcherGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var replacements = new List<FileSystemWatcher>();
            try
            {
                foreach (var root in selected)
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 8192
                    };
                    replacements.Add(watcher);
                    watcher.Created += OnFileChange;
                    watcher.Changed += OnFileChange;
                    watcher.Deleted += OnFileChange;
                    watcher.Renamed += OnRename;
                    watcher.Error += (_, e) => { SignalFileChange(); Report(e.GetException()); };
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch { foreach (var watcher in replacements) watcher.Dispose(); throw; }
            foreach (var watcher in files) watcher.Dispose();
            files = replacements;
        }
    }

    public void Dispose()
    {
        lock (watcherGate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var watcher in files) watcher.Dispose();
            files.Clear();
            stop.Set();
        }
        if (Thread.CurrentThread != thread) thread.Join();
    }

    private void Run()
    {
        try
        {
            WaitHandle[] handles = [stop, .. registry.Select(watcher => watcher.Signal), fileChanged];
            var previous = Snapshot.Read();
            long? due = null;
            var fileDirty = false;
            while (true)
            {
                var timeout = due is null ? Timeout.Infinite : (int)Math.Clamp(due.Value - Environment.TickCount64, 0, int.MaxValue);
                var signal = WaitHandle.WaitAny(handles, timeout);
                if (signal == 0) return;
                if (signal == WaitHandle.WaitTimeout)
                {
                    var current = Snapshot.Read();
                    if (fileDirty || current != previous) Dispatch();
                    previous = current; fileDirty = false; due = null;
                    continue;
                }
                if (signal == handles.Length - 1) fileDirty = true;
                else registry[signal - 1].Arm();
                due = Environment.TickCount64 + (long)debounce.TotalMilliseconds;
            }
        }
        catch (Exception error) { if (!disposed) Report(error); }
        finally
        {
            foreach (var watcher in registry) watcher.Dispose();
            lock (watcherGate)
            {
                foreach (var watcher in files) watcher.Dispose();
                files.Clear();
                disposed = true;
                fileChanged.Dispose(); stop.Dispose();
            }
        }
    }

    private void OnFileChange(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (IsJavaFile(name) || e.ChangeType == WatcherChangeTypes.Created && Directory.Exists(e.FullPath)
            || e.ChangeType == WatcherChangeTypes.Deleted) SignalFileChange();
    }

    private void OnRename(object sender, RenamedEventArgs e)
    {
        if (IsJavaFile(Path.GetFileName(e.FullPath)) || IsJavaFile(Path.GetFileName(e.OldFullPath)) || Directory.Exists(e.FullPath) || !Path.HasExtension(e.FullPath)) SignalFileChange();
    }

    private static bool IsJavaFile(string name) => name.Equals("release", StringComparison.OrdinalIgnoreCase) || name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase);
    private void SignalFileChange() { lock (watcherGate) { if (!disposed) fileChanged.Set(); } }
    private void Dispatch() => ThreadPool.QueueUserWorkItem(_ => { if (disposed) return; try { changed(); } catch (Exception error) { Report(error); } });
    private void Report(Exception error) { try { Error?.Invoke(error); } catch { } }

    private sealed record Snapshot(string? UserHome, string? UserPath, string? MachineHome, string? MachinePath)
    {
        public static Snapshot Read() => new(
            Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.Machine), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
    }

    private sealed class RegistryWatch : IDisposable
    {
        private readonly RegistryHive hive;
        private readonly string path;
        private RegistryKey? key;
        public AutoResetEvent Signal { get; } = new(false);
        public RegistryWatch(RegistryHive hive, string path)
        {
            this.hive = hive; this.path = path;
            try { Arm(); } catch { Signal.Dispose(); throw; }
        }
        public void Arm()
        {
            key?.Dispose(); key = null;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            var candidate = path;
            while (true)
            {
                key = candidate.Length == 0 ? RegistryKey.OpenBaseKey(hive, RegistryView.Registry64) : root.OpenSubKey(candidate);
                if (key is not null) break;
                var separator = candidate.LastIndexOf('\\');
                candidate = separator < 0 ? "" : candidate[..separator];
            }
            var result = RegNotifyChangeKeyValue(key.Handle, false, 1 | 4 | 0x10000000, Signal.SafeWaitHandle, true);
            if (result != 0) { key.Dispose(); key = null; throw new Win32Exception(result); }
        }
        public void Dispose() { key?.Dispose(); key = null; Signal.Dispose(); }
        [DllImport("advapi32.dll")] private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter, SafeWaitHandle signal, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
    }
}
