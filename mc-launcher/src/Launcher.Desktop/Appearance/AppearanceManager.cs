using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Imaging;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
using Launcher.Application.Settings;

namespace Launcher.App.Appearance;

public sealed class AppearanceManager(string directory, IResourceDictionary resources)
{
    public string DataDirectory => directory;
    public string ImageDirectory => Path.Combine(directory,"appearance-data");
    public AppearanceSettings Read() => AppearanceStore.Read(directory);
    private readonly Dictionary<string,SolidColorBrush> _brushes=new(StringComparer.Ordinal);

    public void Apply(ThemeDefinition theme) => Apply(theme, true);
    public void Apply(ThemeDefinition theme, bool animate)
    {
        var colors=ThemeCatalog.Resolve(theme);
        var duration=animate && Launcher.Desktop.MotionPreferences.Enabled ? TimeSpan.FromMilliseconds(260) : TimeSpan.Zero;
        foreach(var pair in colors)
        {
            var target=Color.Parse(pair.Value);
            if(!_brushes.TryGetValue(pair.Key,out var brush))
            {
                brush=new SolidColorBrush(target); _brushes[pair.Key]=brush; resources[pair.Key]=brush;
            }
            else
            {
                brush.Transitions=new Transitions { new ColorTransition { Property=SolidColorBrush.ColorProperty, Duration=duration, Easing=new CubicEaseOut() } };
                brush.Color=target;
            }
        }
        var bg=Color.Parse(colors["BgBrush"]);
        if(Avalonia.Application.Current is { } app) app.RequestedThemeVariant=(bg.R+bg.G+bg.B)>420 ? ThemeVariant.Light : ThemeVariant.Dark;
        resources["PrimaryTextBrush"]=new SolidColorBrush((bg.R+bg.G+bg.B)>420 ? Colors.White : Colors.Black);
        var text=Color.Parse(colors["TextBrush"]);
        var accent=Color.Parse(colors["AccentBrush"]);
        resources["GlassEdgeBrush"]=new LinearGradientBrush { StartPoint=new RelativePoint(0,0,RelativeUnit.Relative), EndPoint=new RelativePoint(1,1,RelativeUnit.Relative), GradientStops=new GradientStops { new(Color.FromArgb(70,text.R,text.G,text.B),0),new(Color.FromArgb(12,text.R,text.G,text.B),0.6),new(Color.FromArgb(30,text.R,text.G,text.B),1) } };
        resources["BackdropGlowBrush"]=new LinearGradientBrush { StartPoint=new RelativePoint(1,0,RelativeUnit.Relative), EndPoint=new RelativePoint(0,1,RelativeUnit.Relative), GradientStops=new GradientStops { new(Color.FromArgb(24,accent.R,accent.G,accent.B),0), new(Colors.Transparent,0.7) } };
    }
    public static BitmapSource Decode(byte[] bytes)
    {
        if(bytes.Length > 24 * 1024 * 1024) throw new ArgumentException("壁纸不能超过 24 MB。");
        using var stream=new MemoryStream(bytes);
        return Bitmap.DecodeToWidth(stream,1600);

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
