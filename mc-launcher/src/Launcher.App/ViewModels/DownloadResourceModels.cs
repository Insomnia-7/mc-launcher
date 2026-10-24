using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Application.Mods;
using Launcher.Domain.Content;

namespace Launcher.App.ViewModels;

public sealed partial class DownloadItem(string source,DownloadCategory category,string id,string title,string author,string metaText,string description) : ObservableObject
{
    public string Source {get;}=source;
    public DownloadCategory Category {get;}=category;
    public string Id {get;}=id;
    public string Title {get;}=title;
    public string Author {get;}=author;
    public string MetaText=>metaText.Length==0?SummaryText:metaText;
    public string Description {get;}=description;
    public string Slug {get;init;}="";
    public string? IconUrl {get;init;}
    public string LoadersText {get;init;}="加载器未标注";
    public string DownloadsText {get;init;}="下载量未标注";
    public string UpdatedText {get;init;}="更新未标注";
    public string VersionsText {get;init;}="";
    public IReadOnlyList<string> SupportedGameVersions {get;init;}=[];
    public System.Windows.Input.ICommand? OpenCommand {get;init;}
    public string Badge=>Source==DownloadViewModel.CurseForgeSource?"CF":"MR";
    public string DisplayTitle=>ResourceChinese.Lookup(Slug,Title).Name??Title;
    public bool HasOriginalTitle=>DisplayTitle!=Title;
    public string DescriptionText=>TranslatedDescription??ResourceChinese.Lookup(Slug,Title).Summary??Description;
    public string TranslationHint=>TranslatedDescription is not null ? "中文简介 · MCIM 自动翻译"
        : ResourceChinese.Lookup(Slug,Title).Summary is not null ? "中文简介 · 内置说明"
        : ResourceChinese.HasChinese(Description) ? "中文简介" : "暂无中文简介 · 显示原文";
    public string SummaryText=>$"{LoadersText} · {DownloadsText} · {UpdatedText}";
    [ObservableProperty] private byte[]? _iconBytes;
    [ObservableProperty] private string? _translatedDescription;
    partial void OnTranslatedDescriptionChanged(string? value) { OnPropertyChanged(nameof(DescriptionText));OnPropertyChanged(nameof(TranslationHint)); }
    public override string ToString()=>DisplayTitle;
}

public sealed partial class ResourceReleaseItem : ObservableObject
{
    public required string Id {get;init;}
    public required string Name {get;init;}
    public required string FileName {get;init;}
    public required IReadOnlyList<string> GameVersions {get;init;}
    public required IReadOnlyList<string> Loaders {get;init;}
    public required string Metadata {get;init;}
    public ModrinthVersion? Modrinth {get;init;}
    public CfFile? CurseForge {get;init;}
    public System.Windows.Input.ICommand? SelectCommand {get;init;}
    public System.Windows.Input.ICommand? InstallCommand {get;init;}
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private string _compatibilityHint="";
    public string ReleaseChannel=>Modrinth is { } mr ? mr.VersionType?.ToLowerInvariant() switch
        { "release"=>"正式版", "beta"=>"测试版", "alpha"=>"开发版", _=>"未标注类型" }
        : CurseForge?.ReleaseType switch { 1=>"正式版",2=>"测试版",3=>"开发版",_=>"未标注类型" };
    public int ChannelOrder=>ReleaseChannel switch {"正式版"=>0,"测试版"=>1,"开发版"=>2,_=>3};
    public bool IsBeta=>ReleaseChannel=="测试版";
    public bool IsAlpha=>ReleaseChannel=="开发版";
    public string DownloadText=>ReleaseChannel=="未标注类型"?"下载此版本":"下载"+ReleaseChannel;
    public string LoaderText=>Loaders.Count==0?"不限定加载器":string.Join(" / ",Loaders.Select(ResourceChinese.Loader));
    public string GameText=>string.Join(", ",GameVersions.Take(4))+(GameVersions.Count>4?$" 等 {GameVersions.Count} 个版本":"");
    public string Header=>$"{ReleaseChannel} · {LoaderText} · {GameText}";
}

public sealed partial class ResourceReleaseGroup(string header,IReadOnlyList<ResourceReleaseItem> releases) : ObservableObject
{
    public string Header {get;}=header;
    public IReadOnlyList<ResourceReleaseItem> Releases {get;}=releases;
    [ObservableProperty] private bool _isExpanded;
}
