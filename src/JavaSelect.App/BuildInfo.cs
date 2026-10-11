using System.Reflection;

namespace JavaSelect.App;

internal static class BuildInfo
{
    public static string Version { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
