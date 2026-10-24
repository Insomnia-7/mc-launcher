using System.Text.Json;

namespace Launcher.Infrastructure.Loaders;

internal static class InstallerJavaRequirement
{
    public static int Read(string gameRoot, string minecraftVersion, int fallback)
    {
        var path = Path.Combine(gameRoot, "versions", minecraftVersion, minecraftVersion + ".json");
        if (!File.Exists(path)) return fallback;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("javaVersion", out var java)
            && java.TryGetProperty("majorVersion", out var major) && major.TryGetInt32(out var required)
            ? Math.Max(17, required) : fallback;
    }
}
