using System.Globalization;
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

/// <summary>
/// Display order of the Java list, the launcher and the shortcut pages. Automatic modes sort on the fly and never rewrite the
/// stored list; "manual" shows the stored order, which the user changes with move up / move down.
/// </summary>
public static class InstallationOrder
{
    public const string DefaultMode = "versionDesc";
    public const string Manual = "manual";
    public static IReadOnlyList<string> Modes { get; } = [DefaultMode, "versionAsc", "name", Manual];

    public static string Normalize(string? mode) => Modes.Contains(mode ?? "") ? mode! : DefaultMode;

    public static IReadOnlyList<JavaInstallation> Sort(IEnumerable<JavaInstallation> items, string? mode, CultureInfo? culture = null)
    {
        var list = items.ToList();
        culture ??= CultureInfo.CurrentCulture;
        Comparison<JavaInstallation>? primary = mode switch
        {
            "versionDesc" => (a, b) => CompareVersion(b, a),
            "versionAsc" => CompareVersion,
            "name" => (a, b) => NaturalCompare(a.DisplayName, b.DisplayName, culture),
            _ => null
        };
        if (primary is null) return list;
        // Unavailable entries sink to the bottom; LINQ ordering is stable, so equal entries keep the order they were added in.
        return list.OrderBy(x => x.IsValid ? 0 : 1)
            .ThenBy(x => x, Comparer<JavaInstallation>.Create(primary))
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Vendor, StringComparer.Create(culture, ignoreCase: true))
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CompareVersion(JavaInstallation a, JavaInstallation b)
    {
        var major = a.MajorVersion.CompareTo(b.MajorVersion);
        if (major != 0) return major;
        var left = VersionParts(a.Version); var right = VersionParts(b.Version);
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var result = (index < left.Count ? left[index] : 0).CompareTo(index < right.Count ? right[index] : 0);
            if (result != 0) return result;
        }
        return 0;
    }

    // "21.0.5+11" -> 21, 0, 5, 11 and "1.8.0_392" -> 1, 8, 0, 392, so 21.0.10 outranks 21.0.5.
    private static List<int> VersionParts(string? version)
    {
        var parts = new List<int>(); long number = -1;
        foreach (var character in version ?? "")
        {
            if (char.IsAsciiDigit(character)) number = Math.Min((number < 0 ? 0 : number) * 10 + (character - '0'), int.MaxValue);
            else if (number >= 0) { parts.Add((int)number); number = -1; }
        }
        if (number >= 0) parts.Add((int)number);
        return parts;
    }

    // Digit runs compare as numbers (Java 8 < Java 17 < Java 21); everything else compares as culture-aware text.
    internal static int NaturalCompare(string? left, string? right, CultureInfo culture)
    {
        left ??= ""; right ??= "";
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            var leftDigit = char.IsAsciiDigit(left[i]); var rightDigit = char.IsAsciiDigit(right[j]);
            var endLeft = RunEnd(left, i, leftDigit); var endRight = RunEnd(right, j, rightDigit);
            int result;
            if (leftDigit && rightDigit)
            {
                var a = left.AsSpan(i, endLeft - i).TrimStart('0'); var b = right.AsSpan(j, endRight - j).TrimStart('0');
                result = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.CompareTo(b, StringComparison.Ordinal);
            }
            else if (leftDigit != rightDigit) result = leftDigit ? -1 : 1;
            else result = culture.CompareInfo.Compare(left.AsSpan(i, endLeft - i), right.AsSpan(j, endRight - j), CompareOptions.IgnoreCase);
            if (result != 0) return result;
            i = endLeft; j = endRight;
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }

    private static int RunEnd(string value, int start, bool digits)
    {
        var end = start;
        while (end < value.Length && char.IsAsciiDigit(value[end]) == digits) end++;
        return end;
    }
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
