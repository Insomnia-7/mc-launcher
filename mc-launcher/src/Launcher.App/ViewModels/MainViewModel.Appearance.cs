using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Appearance;
using Launcher.App.Animations;
using Launcher.Application.Settings;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed record MotionChoice(string Id, string Name)
{
    public override string ToString() => Name;   // UIA / 无障碍读屏读的是 ToString，不给就念成 record 转储
}

public sealed record ThemeChoice(string Id,ThemeDefinition Definition)
{
    public string Name => Definition.Name;
    public override string ToString() => Name;

    // 卡片预览用真实主题色（不是统一拿强调色调透明度），一眼就能看出层次
    public Brush PanelBrush => Pick("PanelBrush");
    public Brush CardBrush => Pick("CardBrush");
    public Brush LineBrush => Pick("LineBrush");

    private Brush Pick(string key)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeCatalog.Resolve(Definition)[key]));
        brush.Freeze();
        return brush;
    }
    public Brush Swatch => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeCatalog.Resolve(Definition)["BgBrush"]));
    public Brush Accent => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeCatalog.Resolve(Definition)["AccentBrush"]));
}

public sealed partial class MainViewModel
{
    private AppearanceManager _appearance=null!;
    private AppearanceSettings _appearanceSettings=new();
    private DispatcherTimer? _appearanceSaveTimer;
    private bool _readingAppearance;
    private int _wallpaperRevision;
    /// <summary>切主题时给出旧底色，窗口铺一层淡出，避免整屏瞬间换色。</summary>
    public event Action<Color>? AppearanceCrossfade;
    public IReadOnlyList<MotionChoice> MotionChoices { get; } = new[] { new MotionChoice("smooth","流畅动画"),new MotionChoice("system","跟随系统"),new MotionChoice("off","关闭动画") };
    [ObservableProperty] private MotionChoice? _selectedMotion;
    public ObservableCollection<ThemeChoice> ThemeChoices { get; }=[];
    [ObservableProperty] private ThemeChoice? _selectedTheme;
    [ObservableProperty] private bool _isAppearanceOpen;
    [ObservableProperty] private string _appearanceHint="主题即时生效；壁纸只从本机导入。";
    [ObservableProperty] private BitmapSource? _wallpaperImage;
    [ObservableProperty] private double _wallpaperOpacity=0.55;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAppearance))]
    private bool _isWallpaperBusy;
    public bool CanEditAppearance => !IsWallpaperBusy;

    private void InitializeAppearance()
    {
        _readingAppearance=true;
        _appearance=new(AppPaths.DataDirectory,System.Windows.Application.Current.Resources);
        _appearance.Crossfade+=color=>AppearanceCrossfade?.Invoke(color);
        _appearanceSettings=_appearance.Read();
        foreach(var theme in ThemeCatalog.BuiltIns) ThemeChoices.Add(new(theme.Id,theme.Definition));
        if(_appearanceSettings.CustomTheme is { } custom) ThemeChoices.Add(new("custom",custom));
        SelectedTheme=ThemeChoices.Single(t=>t.Id==_appearanceSettings.ThemeId);
        WallpaperOpacity=_appearanceSettings.WallpaperOpacity;
        SelectedMotion=MotionChoices.Single(m=>m.Id==_appearanceSettings.MotionMode);
        MotionPreferences.Mode=_appearanceSettings.MotionMode;
        _appearance.Apply(_appearanceSettings.Theme);
        _readingAppearance=false;
    }
    public async Task LoadAppearanceWallpaperAsync()
    {
        var revision=++_wallpaperRevision;
        try { var image=await _appearance.LoadWallpaperAsync(_appearanceSettings); if(revision==_wallpaperRevision) WallpaperImage=image; }
        catch(Exception) { AppearanceHint="壁纸暂时无法读取，可以重新导入。"; }
    }
    partial void OnSelectedThemeChanged(ThemeChoice? value)
    {
        if(_readingAppearance || value is null) return;
        try
        {
            _appearanceSettings=_appearanceSettings with { ThemeId=value.Id };
            _appearance.Apply(value.Definition);
            SaveAppearance();
        }
        catch(Exception ex)
        {
            Serilog.Log.Error(ex,"切换主题失败：{Theme}",value.Id);
            AppearanceHint="切换主题失败："+ex.Message;
        }
    }
    partial void OnSelectedMotionChanged(MotionChoice? value)
    {
        if(_readingAppearance || value is null) return;
        MotionPreferences.Mode=value.Id;
        _appearanceSettings=_appearanceSettings with { MotionMode=value.Id };
        SaveAppearance();
    }
    partial void OnWallpaperOpacityChanged(double value)
    {
        if(_readingAppearance) return;
        _appearanceSettings=_appearanceSettings with { WallpaperOpacity=value };
        _appearanceSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300),DispatcherPriority.Background,(_,_)=>SaveAppearance(),Dispatcher.CurrentDispatcher);
        _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Start();
    }
    [RelayCommand] private void OpenAppearance() { IsAppearanceOpen=true; }
    [RelayCommand] private void CloseAppearance() { if(IsWallpaperBusy) return; if(TrySaveAppearance()) IsAppearanceOpen=false; }
    [RelayCommand] private void SaveAppearance() => TrySaveAppearance();
    /// <summary>把当前主题的配色再同步一次到窗口标题栏（首帧、SourceInitialized、切主题时都要）。</summary>
    public void RefreshWindowChrome()
    {
        try { Appearance.WindowChrome.Sync(ThemeCatalog.Resolve(_appearanceSettings.Theme)); }
        catch (Exception ex) { Serilog.Log.Debug(ex, "同步标题栏配色失败"); }
    }

    public void FlushAppearance() { if(_appearanceSaveTimer?.IsEnabled is true) TrySaveAppearance(); }
    private bool TrySaveAppearance()
    {
        _appearanceSaveTimer?.Stop();
        try { AppearanceStore.Write(AppPaths.DataDirectory,_appearanceSettings); AppearanceHint="外观已保存，下次打开会恢复。"; return true; }
        catch(Exception) { AppearanceHint="外观保存失败，请检查数据目录权限或磁盘空间；当前预览仍可使用。"; return false; }
    }
    public void ImportAppearanceTheme(string path)
    {
        var theme=AppearanceStore.Import(path);
        var next=_appearanceSettings with { ThemeId="custom",CustomTheme=theme };
        AppearanceStore.Write(AppPaths.DataDirectory,next);
        _readingAppearance=true;
        var previous=ThemeChoices.FirstOrDefault(t=>t.Id=="custom"); if(previous is not null) ThemeChoices.Remove(previous);
        var choice=new ThemeChoice("custom",theme); ThemeChoices.Add(choice); SelectedTheme=choice;
        _appearanceSettings=next; _appearance.Apply(theme); _readingAppearance=false;
        AppearanceHint="自定义主题已导入并保存。";
    }
    [RelayCommand] private void ImportTheme()
    {
        var dialog=new OpenFileDialog { Title="导入主题",Filter="主题文件 (*.json)|*.json",CheckFileExists=true };
        if(dialog.ShowDialog()!=true) return;
        try { ImportAppearanceTheme(dialog.FileName); }
        catch(Exception) { AppearanceHint="主题导入失败，请选择有效的 JSON 主题；当前主题已保留。"; }
    }
    [RelayCommand] private void ExportTheme()
    {
        var dialog=new SaveFileDialog { Title="导出主题",Filter="主题文件 (*.json)|*.json",FileName="my-launcher-theme.json" };
        if(dialog.ShowDialog()!=true || SelectedTheme is null) return;
        try { AppearanceStore.Export(dialog.FileName,new ThemeDefinition(1,SelectedTheme.Name,ThemeCatalog.Resolve(SelectedTheme.Definition))); AppearanceHint="主题已导出（不包含壁纸路径、账号或个人设置）。"; }
        catch(Exception) { AppearanceHint="导出失败，请检查所选位置是否可写。"; }
    }
    public async Task ImportAppearanceWallpaperAsync(string path)
    {
        if(IsWallpaperBusy) return;
        IsWallpaperBusy=true; AppearanceHint="正在准备壁纸…"; ++_wallpaperRevision;
        try { var imported=await _appearance.ImportWallpaperAsync(path,_appearanceSettings); _appearanceSettings=imported.Settings; WallpaperImage=imported.Image; AppearanceHint="壁纸已导入，调整下方强度可改变显示效果。"; }
        finally { IsWallpaperBusy=false; }
    }
    [RelayCommand] private async Task ImportWallpaperAsync()
    {
        var dialog=new OpenFileDialog { Title="导入壁纸",Filter="图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",CheckFileExists=true };
        if(dialog.ShowDialog()!=true) return;
        try { await ImportAppearanceWallpaperAsync(dialog.FileName); }
        catch(Exception) { AppearanceHint="壁纸导入失败，请选择 24 MB 以内的有效图片；原壁纸已保留。"; }
    }
    [RelayCommand] private void RemoveWallpaper()
    {
        if(IsWallpaperBusy) return;
        var previous=_appearanceSettings.WallpaperFile;
        var next=_appearanceSettings with { WallpaperFile=null };
        try { AppearanceStore.Write(AppPaths.DataDirectory,next); _appearanceSettings=next; ++_wallpaperRevision; WallpaperImage=null; _appearance.DeleteWallpaper(previous); AppearanceHint="已恢复主题背景。"; }
        catch(Exception) { AppearanceHint="移除壁纸失败，原设置已保留。"; }
    }
    [RelayCommand] private void CloseTopOverlay()
    {
        if(IsConfirmOpen) ConfirmCancelCommand.Execute(null);
        else if(IsAppearanceOpen) CloseAppearanceCommand.Execute(null);
        else if(IsNetworkOpen) CloseNetworkCommand.Execute(null);
        else if(IsAccountsOpen) CloseAccountsCommand.Execute(null);
        else if(IsSetupOpen) CloseSetupCommand.Execute(null);
    }
}