using System.Reflection;

namespace GeBar.Core;

public enum ReleaseChannel { Public, Dev }

public static class BuildChannel
{
    public static ReleaseChannel Current => string.Equals(
        typeof(BuildChannel).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ReleaseChannel")?.Value,
        "Public", StringComparison.OrdinalIgnoreCase) ? ReleaseChannel.Public : ReleaseChannel.Dev;

    public static string Repository(ReleaseChannel channel) => channel == ReleaseChannel.Public ? "loismedia/g-eBAR" : "loismedia/pg";
}
