using System.Reflection;

namespace LocalCam.Desktop;

internal static class ApplicationReleaseInfo
{
    public static Version CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        }
    }

    public static string? GitHubRepository
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(
                    attribute.Key,
                    "GitHubRepository",
                    StringComparison.Ordinal))
                ?.Value;
        }
    }
}
