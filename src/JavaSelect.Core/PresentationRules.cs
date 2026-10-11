using System.Text;

namespace JavaSelect.Core;

public static class LocaleResolver
{
    public static IReadOnlyList<string> Supported { get; } = ["zh-CN", "zh-TW", "en-US", "ja-JP"];
    public static string Resolve(string? language)
    {
        var parts = (language ?? "").ToLowerInvariant().Replace('_', '-').Split('-');
        return parts[0] switch
        {
            "zh" when parts.Contains("hant") => "zh-TW",
            "zh" when parts.Contains("hans") => "zh-CN",
            "zh" when parts.Any(x => x is "tw" or "hk" or "mo") => "zh-TW",
            "zh" => "zh-CN", "ja" => "ja-JP", _ => "en-US"
        };
    }
}

public static class InstallationFilter
{
    public static IReadOnlyList<string> GetVendors(IEnumerable<JavaInstallation> items) => items.Select(x => x.Vendor.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    public static IEnumerable<JavaInstallation> Filter(IEnumerable<JavaInstallation> items, string? query, string? vendor = null)
    {
        var terms = Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return items.Where(x => (string.IsNullOrEmpty(vendor) || string.Equals(x.Vendor, vendor, StringComparison.OrdinalIgnoreCase))
            && terms.All(t => Normalize($"{x.Alias} {x.Version} {x.MajorVersion} {x.Vendor} {x.Kind} {x.Path} Java{x.MajorVersion} {x.Kind}{x.MajorVersion}").Contains(t)));
    }
    private static string Normalize(string? value) => (value ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('/', '\\');
}

public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey, string Text)
{
    public static bool TryParse(string? value, out HotkeyGesture? gesture)
    {
        gesture = null;
        var parts = (value ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            uint flag = part.ToUpperInvariant() switch { "ALT" => 1, "CTRL" or "CONTROL" => 2, "SHIFT" => 4, "WIN" => 8, _ => 0 };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        var key = parts[^1].ToUpperInvariant();
        uint virtualKey = key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) ? key[0] : (uint)(key switch
        {
            "SPACE" => 0x20, "ENTER" => 0x0D, "HOME" => 0x24, "END" => 0x23,
            "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27,
            "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, _ => 0
        });
        if (virtualKey == 0 && key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24) virtualKey = (uint)(0x70 + f - 1);
        if (virtualKey == 0) return false;
        List<string> names = [];
        if ((modifiers & 2) != 0) names.Add("Ctrl");
        if ((modifiers & 1) != 0) names.Add("Alt");
        if ((modifiers & 4) != 0) names.Add("Shift");
        if ((modifiers & 8) != 0) names.Add("Win");
        names.Add(key);
        gesture = new(modifiers, virtualKey, string.Join('+', names));
        return true;
    }
}
