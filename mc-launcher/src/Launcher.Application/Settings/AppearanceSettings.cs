using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Application.Settings;

public sealed record ThemeDefinition(int Schema, string Name, Dictionary<string,string> Colors)
{
    public ThemeDefinition Validate()
    {
        if (Schema != 1 || string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 40 || Name.Any(char.IsControl) || Colors is null || Colors.Count == 0 || Colors.Count > ThemeCatalog.ColorKeys.Count)
            throw new ArgumentException("主题格式无效：需要 schema=1、名称和颜色表。");
        foreach (var pair in Colors)
            if (!ThemeCatalog.ColorKeys.Contains(pair.Key) || !Regex.IsMatch(pair.Value ?? "", "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
                throw new ArgumentException("主题包含未知颜色项或无效色值，请使用 #RRGGBB 或 #AARRGGBB。");
        return this with { Name = Name.Trim(), Colors = new(Colors, StringComparer.Ordinal) };
    }
}

public sealed record ThemePreset(string Id, ThemeDefinition Definition);

public static class ThemeCatalog
{
    public static IReadOnlySet<string> ColorKeys { get; } = new HashSet<string>(new[] {
        "BgBrush","PanelBrush","CardBrush","FieldBrush","HoverBrush","LineBrush","TextBrush","MutedBrush","FaintBrush",
        "AccentBrush","AccentHoverBrush","AccentFillBrush","AccentPressedBrush","SelectedBrush","SuccessBrush","WarnBrush","DangerBrush","DangerSoftBrush","OverlayBrush","WallpaperTintBrush"
    }, StringComparer.Ordinal);
    private static ThemePreset Make(string id,string name,string bg,string panel,string card,string field,string hover,string line,string text,string muted,string accent,string fill,string tint) =>
        new(id,new(1,name,new() {
            ["BgBrush"]=bg,["PanelBrush"]=panel,["CardBrush"]=card,["FieldBrush"]=field,["HoverBrush"]=hover,["LineBrush"]=line,
            ["TextBrush"]=text,["MutedBrush"]=muted,["FaintBrush"]=muted,["AccentBrush"]=accent,["AccentHoverBrush"]=fill,
            ["AccentFillBrush"]=fill,["AccentPressedBrush"]=fill,["SelectedBrush"]="#30"+accent[1..],
            ["SuccessBrush"]="#4CAF95",["WarnBrush"]="#D7A74D",["DangerBrush"]="#E36D76",["DangerSoftBrush"]="#25E36D76",
            ["OverlayBrush"]="#99080D18",["WallpaperTintBrush"]=tint
        }));
    public static IReadOnlyList<ThemePreset> BuiltIns { get; } = new[] {
        Make("ocean","深海玻璃","#111B2D","#E8172338","#CA20304A","#E2293B56","#CC344C6B","#654C6588","#EFF5FF","#BACBE1","#9DC7FF","#3F6DAF","#45111B2D"),
        Make("glacier","冰川微光","#E8EFF8","#E6F6F9FF","#D9FFFFFF","#E8EDF3FB","#E1DDE9F8","#A0A1B5D0","#17263E","#4F617C","#2F5BAE","#3862B0","#30E8EFF8"),
        Make("amethyst","紫晶暮色","#1C172B","#E8251F39","#CF302942","#E139304E","#CC473A60","#737D6B98","#F5EFFF","#CEBFDF","#C7B6FA","#7758AD","#451C172B"),
        Make("forest","松林薄雾","#122425","#E8192E30","#CC203B3B","#E12C4848","#CC375A56","#7372948C","#E9F8F2","#B5D0C7","#90D9BC","#347764","#45122425")
    };
    public static Dictionary<string,string> Resolve(ThemeDefinition theme)
    {
        var result = new Dictionary<string,string>(BuiltIns[0].Definition.Colors, StringComparer.Ordinal);
        foreach (var pair in theme.Validate().Colors) result[pair.Key] = pair.Value;
        if(theme.Colors.ContainsKey("AccentBrush") && !theme.Colors.ContainsKey("SelectedBrush")) {
            var accent=theme.Colors["AccentBrush"];
            result["SelectedBrush"]="#30"+(accent.Length==9?accent[3..]:accent[1..]);
        }
        return result;
    }
}

public sealed record AppearanceSettings(string ThemeId = "ocean", ThemeDefinition? CustomTheme = null, string? WallpaperFile = null, double WallpaperOpacity = 0.55)
{
    public AppearanceSettings Validate()
    {
        if (ThemeId != "custom" && !ThemeCatalog.BuiltIns.Any(t => t.Id == ThemeId)) throw new ArgumentException("未知主题。");
        if (ThemeId == "custom" && CustomTheme is null) throw new ArgumentException("缺少自定义主题。");
        if (!double.IsFinite(WallpaperOpacity) || WallpaperOpacity is < 0 or > 0.85) throw new ArgumentException("壁纸强度应在 0–85% 之间。");
        if (WallpaperFile is not null && !Regex.IsMatch(WallpaperFile,"^[a-f0-9]{32}\\.(?:png|jpg|jpeg|bmp)$")) throw new ArgumentException("壁纸记录无效。");
        return this with { CustomTheme = CustomTheme?.Validate() };
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public ThemeDefinition Theme => ThemeId == "custom" ? CustomTheme! : ThemeCatalog.BuiltIns.Single(t => t.Id == ThemeId).Definition;
}

public static class AppearanceStore
{
    public const int MaxThemeBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true, WriteIndented=true };
    public static string SettingsPath(string directory) => Path.Combine(directory,"appearance.json");
    public static AppearanceSettings Read(string directory)
    {
        try {
            var path=SettingsPath(directory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxThemeBytes * 2) return new();
            return (JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(path),Json) ?? new()).Validate();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public static void Write(string directory, AppearanceSettings settings)
    {
        var valid=settings.Validate();
        Directory.CreateDirectory(directory);
        var path=SettingsPath(directory); var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temp,JsonSerializer.Serialize(valid,Json)); File.Move(temp,path,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public static ThemeDefinition Import(string path)
    {
        if(new FileInfo(path).Length > MaxThemeBytes) throw new ArgumentException("主题文件过大，请选择 32 KB 以内的 JSON 主题。");
        return (JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(path),Json) ?? throw new ArgumentException("主题文件为空。" )).Validate();
    }
    public static void Export(string path,ThemeDefinition theme) => File.WriteAllText(path,JsonSerializer.Serialize(theme.Validate(),Json));
}
