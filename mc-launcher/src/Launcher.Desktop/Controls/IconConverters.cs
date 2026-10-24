using System.Globalization;
using Avalonia.Data.Converters;
using Launcher.App.ViewModels;
using Launcher.Domain.Content;
using Launcher.Domain.Versions;

namespace Launcher.Desktop.Controls;

public sealed class InstanceIconConverter : IValueConverter
{
    public object Convert(object? value,Type targetType,object? parameter,CultureInfo culture)
    {
        if(value is not InstanceCard item) return "Tile.LoaderVanilla";
        if(item.IsModpack) return "Tile.LoaderModpack";
        return VersionIdParser.Parse(item.VersionId,item.BaseVersion).Loader switch
        {
            "Fabric"=>"Tile.LoaderFabric", "Quilt"=>"Tile.LoaderQuilt", "Forge"=>"Tile.LoaderForge",
            "NeoForge"=>"Tile.LoaderNeoforge", "OptiFine"=>"Tile.LoaderOptifine",
            _=>item.BaseVersion.Contains('w') || item.BaseVersion.Contains("-pre",StringComparison.OrdinalIgnoreCase) || item.BaseVersion.Contains("-rc",StringComparison.OrdinalIgnoreCase) ? "Tile.VersionSnapshot" : "Tile.LoaderVanilla"
        };
    }
    public object ConvertBack(object? value,Type targetType,object? parameter,CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CategoryIconConverter : IValueConverter
{
    public object Convert(object? value,Type targetType,object? parameter,CultureInfo culture) => value switch
    {
        DownloadCategory.Mod=>"Tile.CatMod", DownloadCategory.Modpack=>"Tile.LoaderModpack",
        DownloadCategory.ResourcePack=>"Tile.CatResourcepack", DownloadCategory.Datapack=>"Tile.CatDatapack",
        DownloadCategory.Shader=>"Tile.CatShader", _=>"Tile.VersionRelease"
    };
    public object ConvertBack(object? value,Type targetType,object? parameter,CultureInfo culture) => throw new NotSupportedException();
}

public sealed class AccountIconConverter : IValueConverter
{
    public object Convert(object? value,Type targetType,object? parameter,CultureInfo culture) => value is true ? "Tile.UserMicrosoft" : "Tile.UserOffline";
    public object ConvertBack(object? value,Type targetType,object? parameter,CultureInfo culture) => throw new NotSupportedException();
}
