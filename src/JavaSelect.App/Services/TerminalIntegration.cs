using System.Text;
using System.Text.RegularExpressions;

namespace JavaSelect.App.Services;

public static class TerminalIntegration
{
    private const string Begin = "# JavaSelect managed integration begin";
    private const string End = "# JavaSelect managed integration end";

    public static void SetEnabled(bool enabled, string configurationDirectory)
    {
        if (!Path.IsPathFullyQualified(configurationDirectory)) throw new ArgumentException("An absolute configuration directory is required.", nameof(configurationDirectory));
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!Path.IsPathFullyQualified(documents)) throw new DirectoryNotFoundException("The current user's Documents folder could not be found.");
        var profiles = new[] { "PowerShell", "WindowsPowerShell" }
            .Select(folder => Path.Combine(documents, folder, "Microsoft.PowerShell_profile.ps1")).ToArray();
        UpdateProfiles(enabled, Path.GetFullPath(configurationDirectory), profiles);
    }

    private static void UpdateProfiles(bool enabled, string configurationDirectory, IReadOnlyList<string> profiles)
    {
        var signal = Path.Combine(configurationDirectory, "terminal-sync.enabled");
        if (!enabled)
        {
            try { File.Delete(signal); }
            catch (DirectoryNotFoundException) { }
            var failures = new List<Exception>();
            foreach (var profile in profiles)
            {
                try
                {
                    var update = Prepare(profile, null);
                    if (update is not null) { BackupOnce(update); Apply(update); }
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException("Synchronization is off, but some PowerShell profiles could not be cleaned up. Close editors using those files and try again.", failures);
            return;
        }
        var escapedSignal = signal.Replace("'", "''");
        var block = $$"""
            {{Begin}}
            if (-not (Get-Variable JavaSelectOriginalPrompt -Scope Global -ErrorAction SilentlyContinue)) {
                $global:JavaSelectOriginalPrompt = $function:prompt
                function global:prompt {
                    if ([IO.File]::Exists('{{escapedSignal}}')) {
                        try {
                            $javaSelectHome = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User')
                            if ($javaSelectHome) { $env:JAVA_HOME = $javaSelectHome }
                        } catch { }
                    }
                    if ($global:JavaSelectOriginalPrompt) { & $global:JavaSelectOriginalPrompt } else { 'PS ' + $PWD + '> ' }
                }
            }
            {{End}}
            """;
        var updates = profiles.Select(profile => Prepare(profile, block)).OfType<ProfileUpdate>().ToArray();
        Directory.CreateDirectory(configurationDirectory);
        foreach (var update in updates)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(update.Path)!);
            BackupOnce(update);
        }
        var applied = new List<ProfileUpdate>();
        try
        {
            foreach (var update in updates) { Apply(update); applied.Add(update); }
            WriteAtomically(signal, Encoding.UTF8.GetBytes("enabled"));
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var update in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (!Matches(update.Path, update.Updated)) throw new IOException($"Profile changed during rollback: {update.Path}");
                    if (update.Original is null) File.Delete(update.Path);
                    else WriteAtomically(update.Path, update.Original);
                }
                catch (Exception rollbackError) { failures.Add(rollbackError); }
            }
            if (failures.Count > 1) throw new AggregateException("Terminal integration could not be saved or completely rolled back.", failures);
            throw;
        }
    }

    private static ProfileUpdate? Prepare(string profile, string? block)
    {
        var original = File.Exists(profile) ? File.ReadAllBytes(profile) : null;
        if (original is null && block is null) return null;
        Encoding encoding = new UTF8Encoding(true, true);
        var contents = "";
        if (original is not null)
        {
            using var reader = new StreamReader(new MemoryStream(original), new UTF8Encoding(false, true), true);
            _ = reader.ReadToEnd();
            encoding = (Encoding)reader.CurrentEncoding.Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            var preamble = encoding.GetPreamble();
            var offset = original.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
            contents = encoding.GetString(original, offset, original.Length - offset);
        }
        var updated = RemoveManagedBlocks(contents);
        if (block is not null)
        {
            var newline = contents.Contains('\n') ? (contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") : Environment.NewLine;
            var separator = updated.Length == 0 || updated.EndsWith('\n') ? "" : newline;
            updated += separator + block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal) + newline;
        }
        if (updated == contents) return null;
        return new ProfileUpdate(profile, original, encoding.GetPreamble().Concat(encoding.GetBytes(updated)).ToArray());
    }

    private static string RemoveManagedBlocks(string contents)
    {
        var insideBlock = false;
        var markerPattern = "^(?:" + Regex.Escape(Begin) + "|" + Regex.Escape(End) + @")\r?$";
        foreach (Match marker in Regex.Matches(contents, markerPattern, RegexOptions.Multiline))
        {
            var isBegin = marker.Value.TrimEnd('\r') == Begin;
            if (isBegin == insideBlock) throw InvalidMarkers();
            insideBlock = isBegin;
        }
        if (insideBlock) throw InvalidMarkers();
        var pattern = "^" + Regex.Escape(Begin) + @"\r?\n.*?^" + Regex.Escape(End) + @"(?:\r?\n|$)";
        return Regex.Replace(contents, pattern, "", RegexOptions.Multiline | RegexOptions.Singleline);
    }

    private static InvalidDataException InvalidMarkers() => new("The JavaSelect block in a PowerShell profile is incomplete. Repair or remove that marked block and try again.");

    private static void BackupOnce(ProfileUpdate update)
    {
        if (!Matches(update.Path, update.Original)) throw new IOException($"PowerShell profile changed before saving: {update.Path}");
        var backup = update.Path + ".javaselect.bak";
        if (update.Original is not null && !File.Exists(backup)) File.Copy(update.Path, backup, overwrite: false);
    }

    private static void Apply(ProfileUpdate update)
    {
        if (!Matches(update.Path, update.Original)) throw new IOException($"PowerShell profile changed before saving: {update.Path}");
        WriteAtomically(update.Path, update.Updated);
    }

    private static bool Matches(string path, byte[]? expected) => expected is null
        ? !File.Exists(path)
        : File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    private static void WriteAtomically(string path, byte[] contents)
    {
        var temporary = path + ".javaselect." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, contents); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ProfileUpdate(string Path, byte[]? Original, byte[] Updated);
}
