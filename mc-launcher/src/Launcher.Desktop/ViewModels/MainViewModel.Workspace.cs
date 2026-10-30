using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Experience;

namespace Launcher.App.ViewModels;

public sealed record LibraryGamePreferences(string Name="",string Group="",bool Favorite=false,string? CoverFile=null,DateTimeOffset? LastPlayed=null);
public sealed record LibraryPreferences(bool Compact,Dictionary<string,LibraryGamePreferences> Games);

public sealed partial class LibraryEntry(InstanceCard instance,LibraryGamePreferences preferences,string status,Bitmap? cover):ObservableObject
{
    public InstanceCard Instance {get;}=instance;
    public string Name=>string.IsNullOrWhiteSpace(preferences.Name)?Instance.DisplayName:preferences.Name;
    public string Meta=>Describe(Instance);
    public static string Describe(InstanceCard instance)
    {
        if(instance.IsModpack)return instance.Subtitle.Contains(instance.BaseVersion,StringComparison.OrdinalIgnoreCase)?instance.Subtitle:instance.BaseVersion+" · "+instance.Subtitle;
        var parsed=Launcher.Domain.Versions.VersionIdParser.Parse(instance.VersionId,instance.BaseVersion);
        return instance.BaseVersion+" · "+parsed.Title("原版");
    }
    public string Group=>preferences.Group;
    public bool Favorite=>preferences.Favorite;
    public string FavoriteText=>Favorite?"★":"☆";
    public DateTimeOffset? LastPlayed=>preferences.LastPlayed;
    public string Icon=>new Launcher.Desktop.Controls.InstanceIconConverter().Convert(Instance,typeof(string),null,System.Globalization.CultureInfo.InvariantCulture)?.ToString()??"Tile.LoaderVanilla";
    public Bitmap? Cover {get;}=cover;
    public bool HasCover=>Cover is not null;
    [ObservableProperty] private string _status=status;
}

public sealed partial class MainViewModel
{
    private LibraryPreferences _libraryPreferences=new(false,new());
    private string _presentationRoot="";
    private readonly List<LibraryEntry> _allLibraryEntries=[];
    private bool _libraryRefreshQueued;
    private bool _updatingLibrary;
    private bool _selectRecentGame;
    [ObservableProperty] private bool _isDetailOpen;
    [ObservableProperty] private int _detailTab;
    [ObservableProperty] private string _libraryQuery="";
    [ObservableProperty] private string _libraryGroup="全部分组";
    [ObservableProperty] private bool _libraryFavoritesOnly;
    [ObservableProperty] private bool _libraryCompact;
    [ObservableProperty] private string _gameDisplayName="";
    [ObservableProperty] private string _gameGroupName="";
    [ObservableProperty] private string _workspaceHint="";
    public ObservableCollection<LibraryEntry> LibraryEntries {get;}=[];
    public ObservableCollection<string> LibraryGroups {get;}=["全部分组","未分组"];
    public string LibraryResultText=>$"{LibraryEntries.Count} / {Instances.Count}";
    public bool LibraryHasNoResults=>LibraryEntries.Count==0&&HasInstances;
    public string ShellPage=>IsPreferencesOpen?"settings":IsExperienceOpen?"experience":IsDetailOpen&&SelectedInstance is not null?"detail":IsInstanceSection?"library":"discover";
    public bool IsLibraryPage=>ShellPage is "library" or "detail";
    public bool IsDiscoverPage=>ShellPage=="discover";
    public bool IsRolePage=>ShellPage=="experience"&&IsWardrobeTab;
    public bool IsTaskPage=>ShellPage=="experience"&&IsToolsTab;
    public bool IsSettingsPage=>ShellPage=="settings";
    public string ExperiencePageTitle=>IsWardrobeTab?"角色":"下载任务";
    public string ExperiencePageCaption=>IsWardrobeTab?"换上喜欢的皮肤，准备下一次出发。":"查看下载进度，暂停、恢复或清理未完成的任务。";
    public bool IsOverviewTab=>DetailTab==0;
    public bool IsModsDetailTab=>DetailTab==1;
    public bool IsBackupsDetailTab=>DetailTab==2;
    public bool IsSettingsDetailTab=>DetailTab==3;
    public bool IsDiagnosticsDetailTab=>DetailTab==4;
    public string SelectedGameName
    {
        get {if(SelectedInstance is not {} instance)return "";var name=PreferencesFor(instance.InstanceId).Name;return string.IsNullOrWhiteSpace(name)?instance.DisplayName:name;}
    }
    public string SelectedGameStatus=>SelectedInstance is {} instance?GameStatus(instance):"";
    public string SelectedGameMeta=>SelectedInstance is {} instance?LibraryEntry.Describe(instance):"";
    public Bitmap? SelectedGameCover=>_allLibraryEntries.FirstOrDefault(e=>e.Instance.InstanceId==SelectedInstance?.InstanceId)?.Cover;
    public bool HasGameCover=>SelectedGameCover is not null;
    public bool HasWorldDisplayName=>SelectedInstance is not null&&SelectedGameName!=WorldVersion;
    private string LibrarySettingsPath=>Path.Combine(AppPaths.DataDirectory,"library",RootKey()+".json");
    private string RootKey()
    {
        var path=Path.GetFullPath(GameRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if(OperatingSystem.IsWindows())path=path.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
    }
    private LibraryGamePreferences PreferencesFor(string id)=>_libraryPreferences.Games.GetValueOrDefault(id)??new();
    private void InitializeWorkspace()
    {
        BeforeMainNavigation=()=>{IsExperienceOpen=false;IsAppearanceOpen=false;IsNetworkOpen=false;IsDetailOpen=false;WorkspaceHint="";};
        GameStarted=id=>
        {
            var preferences=PreferencesFor(id) with {LastPlayed=DateTimeOffset.UtcNow};
            _libraryPreferences.Games[id]=preferences;SaveLibraryPreferences();RebuildLibrary();
        };
        RunningGamesChanged=()=>Dispatcher.UIThread.Post(RefreshGameStatuses);
        Instances.CollectionChanged+=(_,_)=>QueueLibraryRefresh();
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(IsInstanceSection) or nameof(IsDetailOpen) or nameof(IsExperienceOpen) or nameof(ExperienceTab) or nameof(IsAppearanceOpen) or nameof(IsNetworkOpen)) NotifyWorkspaceNavigation();
            if(e.PropertyName==nameof(GameRoot))QueueLibraryRefresh();
            if(e.PropertyName==nameof(SelectedInstance))
            {
                if(SelectedInstance is {} instance){var preferences=PreferencesFor(instance.InstanceId);GameDisplayName=preferences.Name;GameGroupName=preferences.Group;}
                WorkspaceHint="";InspectionIssues.Clear();DiagnosisText="";ShowsNetworkFix=false;
                if(IsDetailOpen)ReloadExperienceLists();
                NotifySelectedGame();
            }
        };
        RebuildLibrary();
    }
    private void NotifyWorkspaceNavigation()
    {
        foreach(var property in new[]{nameof(ShellPage),nameof(IsLibraryPage),nameof(IsDiscoverPage),nameof(IsRolePage),nameof(IsTaskPage),nameof(IsSettingsPage),nameof(ExperiencePageTitle),nameof(ExperiencePageCaption)})OnPropertyChanged(property);
        OnPropertyChanged(nameof(PreviewActive));
    }
    private void NotifySelectedGame()
    {
        foreach(var property in new[]{nameof(SelectedGameName),nameof(SelectedGameMeta),nameof(SelectedGameStatus),nameof(SelectedGameCover),nameof(HasGameCover),nameof(WorldDisplayName),nameof(HasWorldDisplayName)})OnPropertyChanged(property);
    }
    private void QueueLibraryRefresh()
    {
        if(_libraryRefreshQueued)return;_libraryRefreshQueued=true;
        Dispatcher.UIThread.Post(()=>{_libraryRefreshQueued=false;RebuildLibrary();},DispatcherPriority.Background);
    }
    private void RebuildLibrary()
    {
        _updatingLibrary=true;
        var changedRoot=_presentationRoot!=GameRoot;
        if(changedRoot)
        {
            _selectRecentGame=true;
            _presentationRoot=GameRoot;
            _libraryPreferences=ExperienceFiles.Read<LibraryPreferences>(LibrarySettingsPath)??new(false,new());
            LibraryCompact=_libraryPreferences.Compact;
        }
        var previous=_allLibraryEntries.ToArray();_allLibraryEntries.Clear();
        foreach(var instance in Instances)
        {
            var preferences=PreferencesFor(instance.InstanceId);Bitmap? cover=null;
            try{if(preferences.CoverFile is {} file&&File.Exists(ExperienceFiles.Child(AppPaths.DataDirectory,file)))using(var stream=File.OpenRead(ExperienceFiles.Child(AppPaths.DataDirectory,file)))cover=DecodeLibraryCover(stream,480);}
            catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){ }
            _allLibraryEntries.Add(new(instance,preferences,GameStatus(instance),cover));
        }
        var group=LibraryGroup;LibraryGroups.Clear();LibraryGroups.Add("全部分组");LibraryGroups.Add("未分组");
        foreach(var name in _allLibraryEntries.Select(e=>e.Group).Where(g=>g.Length>0).Distinct().Order())LibraryGroups.Add(name);
        LibraryGroup=LibraryGroups.Contains(group)?group:"全部分组";
        if(_selectRecentGame&&_allLibraryEntries.Count>0)
        {
            _selectRecentGame=false;
            var recent=_allLibraryEntries.Where(e=>e.LastPlayed is not null).OrderByDescending(e=>e.LastPlayed).FirstOrDefault();
            if(recent is not null)SelectedInstance=recent.Instance;
            if(SelectedInstance is {} selected){var preferences=PreferencesFor(selected.InstanceId);GameDisplayName=preferences.Name;GameGroupName=preferences.Group;}
        }
        _updatingLibrary=false;FilterLibrary();NotifySelectedGame();
        foreach(var entry in previous)entry.Cover?.Dispose();
    }
    private void FilterLibrary()
    {
        if(_updatingLibrary)return;
        var query=LibraryQuery.Trim();
        var entries=_allLibraryEntries.Where(e=>(!LibraryFavoritesOnly||e.Favorite)&&
            (LibraryGroup=="全部分组"||(LibraryGroup=="未分组"?e.Group.Length==0:e.Group==LibraryGroup))&&
            (query.Length==0||(e.Name+" "+e.Meta).Contains(query,StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e=>e.Favorite).ThenByDescending(e=>e.LastPlayed).ThenBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
        LibraryEntries.Clear();foreach(var entry in entries)LibraryEntries.Add(entry);
        OnPropertyChanged(nameof(LibraryResultText));OnPropertyChanged(nameof(LibraryHasNoResults));
    }
    private void SaveLibraryPreferences()
    {
        try{ExperienceFiles.Write(LibrarySettingsPath,_libraryPreferences with {Compact=LibraryCompact});}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){WorkspaceHint="游戏库偏好保存失败："+ex.Message;}
    }
    private string GameStatus(InstanceCard instance)
    {
        lock(_running)if(_running.ContainsKey(instance.VersionId))return "游戏运行中";
        var last=PreferencesFor(instance.InstanceId).LastPlayed;
        return last is {} date?"上次游玩 "+date.LocalDateTime.ToString("MM-dd HH:mm"):"准备就绪";
    }
    private void RefreshGameStatuses(){foreach(var entry in _allLibraryEntries)entry.Status=GameStatus(entry.Instance);OnPropertyChanged(nameof(SelectedGameStatus));}
    partial void OnLibraryQueryChanged(string value)=>FilterLibrary();
    partial void OnLibraryGroupChanged(string value)=>FilterLibrary();
    partial void OnLibraryFavoritesOnlyChanged(bool value)=>FilterLibrary();
    partial void OnLibraryCompactChanged(bool value){if(!_updatingLibrary)SaveLibraryPreferences();}
    partial void OnDetailTabChanged(int value)
    {
        foreach(var property in new[]{nameof(IsOverviewTab),nameof(IsModsDetailTab),nameof(IsBackupsDetailTab),nameof(IsSettingsDetailTab),nameof(IsDiagnosticsDetailTab)})OnPropertyChanged(property);
    }
    [RelayCommand] private void SetLibraryLayout(string? layout)=>LibraryCompact=layout=="list";
    [RelayCommand] private void FavoriteGame(LibraryEntry? entry)
    {
        if(entry is null)return;var preferences=PreferencesFor(entry.Instance.InstanceId);
        _libraryPreferences.Games[entry.Instance.InstanceId]=preferences with {Favorite=!preferences.Favorite};SaveLibraryPreferences();RebuildLibrary();
    }
    [RelayCommand] private void OpenGame(LibraryEntry? entry)
    {
        if(entry is not null)SelectedInstance=Instances.FirstOrDefault(i=>i.InstanceId==entry.Instance.InstanceId);
        if(SelectedInstance is null)return;
        IsExperienceOpen=false;IsAppearanceOpen=false;IsNetworkOpen=false;IsInstanceSection=true;DetailTab=0;IsDetailOpen=true;
        var preferences=PreferencesFor(SelectedInstance.InstanceId);GameDisplayName=preferences.Name;GameGroupName=preferences.Group;
        ReloadExperienceLists();NotifySelectedGame();
    }
    [RelayCommand] private void SelectDetailTab(string? value){if(int.TryParse(value,out var index)&&index is >=0 and <=4)DetailTab=index;}
    [RelayCommand] private void SaveGamePresentation()
    {
        if(SelectedInstance is not {} instance)return;
        var name=GameDisplayName.Trim();var group=GameGroupName.Trim();
        if(name.Length>80||group.Length>32||group is "全部分组" or "未分组"){WorkspaceHint="名称最多 80 字，分组最多 32 字，请避免使用预留分组名。";return;}
        _libraryPreferences.Games[instance.InstanceId]=PreferencesFor(instance.InstanceId) with {Name=name,Group=group};
        WorkspaceHint="名称和分组已保存。";SaveLibraryPreferences();RebuildLibrary();
    }
    [RelayCommand] private async Task ChooseGameCoverAsync()
    {
        var path=await PlatformUi.PickFileAsync("选择游戏封面","图片|*.png;*.jpg;*.jpeg;*.webp");
        if(path is not null)await ImportGameCoverFileAsync(path);
    }
    public Task ImportGameCoverFileAsync(string path)
    {
        try
        {
            if(SelectedInstance is not {} instance)return Task.CompletedTask;
            if(new FileInfo(path).Length>20*1024*1024)throw new InvalidDataException("请选择小于 20 MB 的封面图片。");
            using var source=File.OpenRead(path);using var bitmap=DecodeLibraryCover(source,960);
            var relative="library/covers/"+RootKey()+"-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.InstanceId)))[..16]+".png";
            var target=ExperienceFiles.Child(AppPaths.DataDirectory,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);bitmap.Save(target);
            _libraryPreferences.Games[instance.InstanceId]=PreferencesFor(instance.InstanceId) with {CoverFile=relative};
            WorkspaceHint="游戏封面已保存。";SaveLibraryPreferences();RebuildLibrary();
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){WorkspaceHint="封面导入失败："+ex.Message;}
        return Task.CompletedTask;
    }
    private static Bitmap DecodeLibraryCover(Stream stream,int width)
    {
        // Avalonia's Skia decoder throws NullReferenceException for invalid image bytes.
        try{return Bitmap.DecodeToWidth(stream,width);}
        catch(NullReferenceException ex){throw new InvalidDataException("图片已损坏，或图片格式不受支持。",ex);}
    }
    [RelayCommand] private void RemoveGameCover()
    {
        if(SelectedInstance is not {} instance)return;
        _libraryPreferences.Games[instance.InstanceId]=PreferencesFor(instance.InstanceId) with {CoverFile=null};SaveLibraryPreferences();RebuildLibrary();WorkspaceHint="已恢复版本图标。";
    }
    [RelayCommand] private void OpenGameResourceFolder(string? folder)
    {
        if(folder is not ("mods" or "resourcepacks" or "shaderpacks"))return;
        if(SelectedInstance is not {} instance||ResolveInstancePath(instance,"instances") is not {} instancePath)return;
        try{var path=Path.Combine(instancePath,".minecraft",folder);Directory.CreateDirectory(path);OpenInExplorer(path);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){WorkspaceHint="无法打开资源目录："+ex.Message;}
    }
}
