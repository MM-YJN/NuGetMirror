using System.Reflection;

using NuGetMirror.Discovery;

namespace NuGetMirror.UnitTests;

internal static class DiscoveryCacheSeeder
{
    public static void SeedSnapshot(DiscoveryCache cache, Dictionary<string, string> forwardMap)
    {
        var snapshot = new DiscoverySnapshot(DateTimeOffset.UtcNow, "{}", forwardMap, []);
        FieldInfo field = typeof(DiscoveryCache).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");
        field.SetValue(cache, snapshot);
    }
}
