using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Application.Settings;

namespace Launcher.App.Appearance;

public sealed class AppearanceManager(string directory, ResourceDictionary resources)
{
    public string DataDirectory => directory;
    public string ImageDirectory => Path.Combine(directory,"appearance-data");
    public AppearanceSettings Read() => AppearanceStore.Read(directory);
    public void Apply(ThemeDefinition theme)
    {
        var colors=ThemeCatalog.Resolve(theme);
        foreach(var pair in colors) {
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            brush.Freeze(); resources[pair.Key]=brush;
        }
        var accent=(Color)ColorConverter.ConvertFromString(colors["AccentBrush"]);
        var glow=new LinearGradientBrush(Color.FromArgb(18,accent.R,accent.G,accent.B),Color.FromArgb(0,accent.R,accent.G,accent.B),45);
        glow.Freeze(); resources["BackdropGlowBrush"]=glow;
        resources["GlassEdgeBrush"]=resources["LineBrush"];
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
