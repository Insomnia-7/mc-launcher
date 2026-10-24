using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Launcher.App.ViewModels;
internal static class PlatformUi
{
    public static bool CanStoreSecrets(Launcher.Application.Abstractions.ISecretProtector? protector) => protector is not null;
    public static Window? Window { get; set; }
    private static IStorageProvider Storage => Window?.StorageProvider ?? throw new InvalidOperationException("窗口尚未就绪。");
    public static async Task<string?> PickFolderAsync(string title, string? initial = null)
    {
        var location = initial is not null ? await Storage.TryGetFolderFromPathAsync(initial) : null;
        var files = await Storage.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false, SuggestedStartLocation = location });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private static List<FilePickerFileType> Filters(string filter)
    {
        var parts = filter.Split('|'); var result = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2) result.Add(new(parts[i]) { Patterns = parts[i + 1].Split(';') });
        return result;
    }
    public static async Task<string?> PickFileAsync(string title, string filter)
    {
        var files = await Storage.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false, FileTypeFilter = Filters(filter) });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public static async Task<string?> SaveFileAsync(string title, string filter, string name)
    {
        var file = await Storage.SaveFilePickerAsync(new() { Title = title, SuggestedFileName = name, FileTypeChoices = Filters(filter), ShowOverwritePrompt = true });
        return file?.TryGetLocalPath();
    }
    public static Task CopyTextAsync(string text) => Window?.Clipboard?.SetTextAsync(text) ?? throw new InvalidOperationException("剪贴板不可用。");
    public static async Task RunOnUiAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);
    public static void Shutdown() => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    public static object? DecodeSkinHead(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var bitmap = new Bitmap(stream);
        if (bitmap.PixelSize.Width < 16 || bitmap.PixelSize.Height < 16) return null;
        var head = new RenderTargetBitmap(new PixelSize(8, 8));
        using (var context = head.CreateDrawingContext()) context.DrawImage(bitmap, new Rect(8,8,8,8), new Rect(0,0,8,8));
        return head;
    }
}
