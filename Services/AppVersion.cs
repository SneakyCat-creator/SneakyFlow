using System.Reflection;

namespace SneakyFlow.Services;

public static class AppVersion
{
    public static Version Number { get; } = GetNumber();
    public static string Display { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Number.ToString();
    public static bool IsPrerelease => Display.Contains('-');

    private static Version GetNumber()
    {
        var version = typeof(AppVersion).Assembly.GetName().Version!;
        return new Version(version.Major, version.Minor, version.Build);
    }
}
