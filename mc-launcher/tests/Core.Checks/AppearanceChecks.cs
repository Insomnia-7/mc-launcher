using System.Text.Json;
using Launcher.Application.Settings;

internal static class AppearanceChecks
{
    public static void Run(Action<string,bool,string?> check)
    {
        void Check(string name,bool ok)=>check(name,ok,null);
        Check("四套内置主题名称和标识独立",ThemeCatalog.BuiltIns.Count==4 && ThemeCatalog.BuiltIns.Select(t=>t.Id).Distinct().Count()==4);
        foreach(var theme in ThemeCatalog.BuiltIns) Check("主题色表完整："+theme.Definition.Name,ThemeCatalog.Resolve(theme.Definition).Count==ThemeCatalog.ColorKeys.Count);
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
            var original=File.ReadAllText(AppearanceStore.SettingsPath(root));
            foreach(var invalid in new[] { settings with { WallpaperFile="../outside.png" },settings with { WallpaperFile="https://example.test/a.png" },settings with { WallpaperOpacity=double.NaN },settings with { WallpaperOpacity=1 },settings with { ThemeId="missing" } }) {
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
}
