using JavaSelect.Core;

namespace JavaSelect.App.Services;

internal static class StartupMaintenance
{
    /// <summary>
    /// Runs as <c>JavaSelect.exe --enable-startup</c> from Setup. Returns 0 only when the sign-in task exists and the
    /// preference is saved, so Setup can tell the user whether startup is really on.
    /// </summary>
    internal static async Task<int> EnableAsync(string executablePath)
    {
        var store = new ConfigStore();
        try
        {
            // Fail before touching Task Scheduler when the settings cannot be read or written back.
            var settings = await store.LoadAsync();
            await Task.Run(() => new StartupService().SetEnabled(true, executablePath));
            settings.StartAtLogin = true;
            try { await store.SaveAsync(settings); }
            catch
            {
                // A task without the matching preference would be removed by the next unrelated save.
                try { await Task.Run(() => new StartupService().SetEnabled(false, executablePath, takeOver: false)); } catch (Exception undo) { Log(store.DirectoryPath, undo); }
                throw;
            }
            return 0;
        }
        catch (Exception error)
        {
            Log(store.DirectoryPath, error);
            return 1;
        }
    }

    private static void Log(string directory, Exception error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "app.log"), DateTimeOffset.Now + " --enable-startup: " + error + Environment.NewLine);
        }
        catch { }
    }
}
