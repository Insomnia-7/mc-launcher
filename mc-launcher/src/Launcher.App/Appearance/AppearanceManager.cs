using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Launcher.Application.Settings;

namespace Launcher.App.Appearance;

public sealed class AppearanceManager(string directory, ResourceDictionary resources)
{
    public string DataDirectory => directory;
    public string ImageDirectory => Path.Combine(directory,"appearance-data");
    public AppearanceSettings Read() => AppearanceStore.Read(directory);
    private readonly Dictionary<string,SolidColorBrush> _brushes=new(StringComparer.Ordinal);

    /// <summary>切主题时给出一层旧底色，窗口铺上去淡出，避免整屏「啪」地换色。</summary>
    public event Action<Color>? Crossfade;

    public void Apply(ThemeDefinition theme) => Apply(theme, animate: true);

    public void Apply(ThemeDefinition theme,bool animate)
    {
        var colors=ThemeCatalog.Resolve(theme);
        var first=_brushes.Count==0;
        var duration=animate && !first && Animations.MotionPreferences.Mode!="off"
            ? TimeSpan.FromMilliseconds(380)
            : TimeSpan.Zero;

        var oldBg=resources["BgBrush"] is SolidColorBrush old ? old.Color : (Color?)null;

        // 颜色本身直接落到目标值：ResourceDictionary 里的画刷会被 WPF 冻结，动画 Color 会抛
        // 「对象已密封」；真正的顺滑交给窗口那层旧底色淡出（Crossfade），观感等价且不失效。
        foreach(var pair in colors) {
            var target=(Color)ColorConverter.ConvertFromString(pair.Value);
            if(_brushes.TryGetValue(pair.Key,out var brush) && !brush.IsFrozen) brush.Color=target;
            else { brush=new SolidColorBrush(target); _brushes[pair.Key]=brush; }
            resources[pair.Key]=brush;
        }

        var accent=(Color)ColorConverter.ConvertFromString(colors["AccentBrush"]);
        var glow=new LinearGradientBrush(Color.FromArgb(18,accent.R,accent.G,accent.B),Color.FromArgb(0,accent.R,accent.G,accent.B),45);
        glow.Freeze(); resources["BackdropGlowBrush"]=glow;
        // 玻璃边：上亮下暗的 1px 渐变，比一圈实色线更像玻璃；颜色取自主题文字色，深浅主题都成立
        var text=(Color)ColorConverter.ConvertFromString(colors["TextBrush"]);
        var edge=new LinearGradientBrush(Color.FromArgb(64,text.R,text.G,text.B),Color.FromArgb(14,text.R,text.G,text.B),90);
        edge.Freeze(); resources["GlassEdgeBrush"]=edge;
        WindowChrome.Sync(colors,duration);
        if(animate && !first && oldBg is { } oldColor) Crossfade?.Invoke(oldColor);
    }
    public static BitmapSource Decode(byte[] bytes)
    {
        if(bytes.Length > 24 * 1024 * 1024) throw new ArgumentException("壁纸不能超过 24 MB。");
        using var stream=new MemoryStream(bytes);
        var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
        image.DecodePixelWidth=1600; image.StreamSource=stream; image.EndInit(); image.Freeze();
        return image;
    }
    public async Task<BitmapSource?> LoadWallpaperAsync(AppearanceSettings settings)
    {
        if(settings.WallpaperFile is null) return null;
        var path=Path.Combine(ImageDirectory,settings.Validate().WallpaperFile!);
        if(!File.Exists(path)) return null;
        return await Task.Run(() => {
            if(new FileInfo(path).Length > 24*1024*1024) throw new ArgumentException("壁纸不能超过 24 MB。");
            return Decode(File.ReadAllBytes(path));
        });
    }
    public async Task<(AppearanceSettings Settings,BitmapSource Image)> ImportWallpaperAsync(string source,AppearanceSettings settings)
    {
        var extension=Path.GetExtension(source).ToLowerInvariant();
        if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new ArgumentException("请选择 PNG、JPG 或 BMP 图片。");
        var bytes=await Task.Run(() => {
            if(new FileInfo(source).Length > 24*1024*1024) throw new ArgumentException("壁纸不能超过 24 MB。");
            return File.ReadAllBytes(source);
        });
        var image=await Task.Run(() => Decode(bytes));
        Directory.CreateDirectory(ImageDirectory);
        var name=Guid.NewGuid().ToString("N")+extension;
        var path=Path.Combine(ImageDirectory,name);
        var next=settings with { WallpaperFile=name };
        try { await File.WriteAllBytesAsync(path,bytes); AppearanceStore.Write(directory,next); }
        catch { if(File.Exists(path)) File.Delete(path); throw; }
        DeleteWallpaper(settings.WallpaperFile);
        return (next,image);
    }
    public void DeleteWallpaper(string? name)
    {
        if(name is null) return;
        try { var valid=new AppearanceSettings(WallpaperFile:name).Validate(); File.Delete(Path.Combine(ImageDirectory,valid.WallpaperFile!)); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}