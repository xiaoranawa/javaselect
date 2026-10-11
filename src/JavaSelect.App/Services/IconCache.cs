using System.Security.Cryptography;
using System.Text;
using JavaSelect.Core;

namespace JavaSelect.App.Services;

internal static class IconCache
{
    internal static string? GetJavaIcon(JavaInstallation java, string configDirectory)
    {
        try
        {
            var exe = Path.Combine(java.Path, "bin", "java.exe"); if (!File.Exists(exe)) return null;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exe.ToUpperInvariant() + java.Fingerprint)))[..24];
            var directory = Path.Combine(configDirectory, "icons"); Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, hash + ".png"); if (File.Exists(destination)) return destination;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); if (icon is null) return null;
            using var bitmap = icon.ToBitmap(); bitmap.Save(destination, System.Drawing.Imaging.ImageFormat.Png); return destination;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException) { return null; }
    }
}
