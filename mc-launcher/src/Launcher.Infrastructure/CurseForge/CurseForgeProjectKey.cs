namespace Launcher.Infrastructure.CurseForge;

/// <summary>Optional release input. A distributed desktop credential is extractable, not a secret vault.</summary>
public static class CurseForgeProjectKey
{
    private static readonly Lazy<string?> Key=new(()=>
    {
        using var stream=typeof(CurseForgeProjectKey).Assembly.GetManifestResourceStream("Launcher.CurseForge.ProjectKey");
        if(stream is null)return null;
        using var reader=new StreamReader(stream);
        var value=reader.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(value)?null:value;
    });
    public static string? Resolve()=>Key.Value;
    public static bool IsConfigured=>Resolve() is not null;
}
