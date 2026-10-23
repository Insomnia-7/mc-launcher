using System.Text.Json;
using Launcher.Application.Settings;

internal static class AppearanceChecks
{
    public static void Run(Action<string,bool,string?> check)
    {
        void Check(string name,bool ok)=>check(name,ok,null);
        Check("内置主题名称和标识独立",ThemeCatalog.BuiltIns.Count>=4 && ThemeCatalog.BuiltIns.Select(t=>t.Id).Distinct().Count()==ThemeCatalog.BuiltIns.Count);
        foreach(var theme in ThemeCatalog.BuiltIns) Check("主题色表完整："+theme.Definition.Name,ThemeCatalog.Resolve(theme.Definition).Count==ThemeCatalog.ColorKeys.Count);
        foreach(var preset in ThemeCatalog.BuiltIns) {
            var colors=ThemeCatalog.Resolve(preset.Definition);
            var bg=Rgb(colors["BgBrush"]);
            var field=Composite(Rgb(colors["FieldBrush"]),bg);
            Check("主题正文对比度："+preset.Definition.Name,Contrast(Rgb(colors["TextBrush"]),bg)>=7);
            Check("主题说明文字对比度："+preset.Definition.Name,Contrast(Rgb(colors["MutedBrush"]),field)>=4.5);
            Check("主题主按钮对比度："+preset.Definition.Name,Contrast(Rgb("#FFFFFF"),Rgb(colors["AccentFillBrush"]))>=4.5);
        }
        var root=Path.Combine(Path.GetTempPath(),"mcl-appearance-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            Check("没有外观文件时保持默认主题",AppearanceStore.Read(root)==new AppearanceSettings());
            File.WriteAllText(Path.Combine(root,"settings.json"),"{\"keep\":true}");
            var custom=new ThemeDefinition(1,"我的主题",new() { ["AccentBrush"]="#AABBCC" });
            var settings=new AppearanceSettings("custom",custom,new string('a',32)+".png",0.7);
            AppearanceStore.Write(root,settings);
            var loaded=AppearanceStore.Read(root);
            Check("自定义配色与壁纸强度可恢复",loaded.ThemeId=="custom" && loaded.CustomTheme!.Colors["AccentBrush"]=="#AABBCC" && loaded.WallpaperOpacity==0.7);
            Check("外观保存不覆盖原有启动器设置",File.ReadAllText(Path.Combine(root,"settings.json"))=="{\"keep\":true}");
            var exported=Path.Combine(root,"export.json"); AppearanceStore.Export(exported,custom);
            Check("导出主题可重新导入",AppearanceStore.Import(exported).Name=="我的主题");
            Check("导出主题不包含壁纸记录和用户配置",!File.ReadAllText(exported).Contains("wallpaper",StringComparison.OrdinalIgnoreCase) && !File.ReadAllText(exported).Contains(root));
            Check("稀疏自定义颜色继承完整基线",ThemeCatalog.Resolve(custom)["TextBrush"]==ThemeCatalog.BuiltIns[0].Definition.Colors["TextBrush"]);
            Check("旧外观配置默认启用流畅动画",loaded.MotionMode=="smooth");
            AppearanceStore.Write(root,settings with { MotionMode="off" });
            Check("动画模式保存与恢复",AppearanceStore.Read(root).MotionMode=="off");
            AppearanceStore.Write(root,settings);
            var original=File.ReadAllText(AppearanceStore.SettingsPath(root));
            foreach(var invalid in new[] { settings with { WallpaperFile="../outside.png" },settings with { WallpaperFile="https://example.test/a.png" },settings with { WallpaperOpacity=double.NaN },settings with { WallpaperOpacity=1 },settings with { ThemeId="missing" }, settings with { MotionMode="unknown" } }) {
                var failed=false;try{AppearanceStore.Write(root,invalid);}catch(ArgumentException){failed=true;}
                Check("无效外观设置不覆盖原文件",failed && File.ReadAllText(AppearanceStore.SettingsPath(root))==original);
            }
            foreach(var invalid in new[] { custom with { Schema=2 },custom with { Name=new string('a',41) },custom with { Colors=new() { ["script"]="#123456" } }, custom with { Colors=new() { ["AccentBrush"]="https://example.test/color" } },custom with { Colors=new() { ["AccentBrush"]="#12" } } }) {
                var rejected=false;try{invalid.Validate();}catch(ArgumentException){rejected=true;} Check("拒绝主题脚本字段或无效格式",rejected);
            }
            var blocked=false;
            using(var held=new FileStream(AppearanceStore.SettingsPath(root),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
                try{AppearanceStore.Write(root,new());}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){blocked=true;}
            }
            Check("外观写入失败保留原文件",blocked && File.ReadAllText(AppearanceStore.SettingsPath(root))==original);
            Check("外观写入失败不遗留临时文件",Directory.GetFiles(root,"*.tmp").Length==0);
            File.WriteAllText(exported,new string(' ',AppearanceStore.MaxThemeBytes+1));
            var oversized=false;try{AppearanceStore.Import(exported);}catch(ArgumentException){oversized=true;} Check("超大主题文件被拒绝",oversized);
            File.WriteAllText(AppearanceStore.SettingsPath(root),"broken"); Check("损坏外观配置不阻断启动",AppearanceStore.Read(root)==new AppearanceSettings());
        } finally {Directory.Delete(root,true);}
    }
    private static (double R,double G,double B,double A) Rgb(string value)
    {
        var offset=value.Length==9?3:1;
        return (Convert.ToInt32(value.Substring(offset,2),16)/255d,Convert.ToInt32(value.Substring(offset+2,2),16)/255d,Convert.ToInt32(value.Substring(offset+4,2),16)/255d,value.Length==9?Convert.ToInt32(value.Substring(1,2),16)/255d:1);
    }
    private static (double R,double G,double B,double A) Composite((double R,double G,double B,double A) fg,(double R,double G,double B,double A) bg) =>
        (fg.R*fg.A+bg.R*(1-fg.A),fg.G*fg.A+bg.G*(1-fg.A),fg.B*fg.A+bg.B*(1-fg.A),1);
    private static double Contrast((double R,double G,double B,double A) a,(double R,double G,double B,double A) b)
    {
        static double Linear(double c)=>c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);
        static double L((double R,double G,double B,double A) c)=>.2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
        var x=L(a);var y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
}
