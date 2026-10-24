using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

// The shared view models call this small platform boundary. Each desktop host supplies its implementation.
internal static class PlatformUi
{
    public static bool CanStoreSecrets(Launcher.Application.Abstractions.ISecretProtector? protector) => true;
    public static Task<string?> PickFolderAsync(string title, string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initial ?? "" };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }
    public static Task<string?> PickFileAsync(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
    public static Task<string?> SaveFileAsync(string title, string filter, string name)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = name, AddExtension = true };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
    public static Task CopyTextAsync(string text) { Clipboard.SetText(text); return Task.CompletedTask; }
    public static Task RunOnUiAsync(Action action) => System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;
    public static void Shutdown() => System.Windows.Application.Current.Shutdown();
    public static object? DecodeSkinHead(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.StreamSource = stream; bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
        if (bitmap.PixelWidth < 16 || bitmap.PixelHeight < 16) return null;
        var head = new CroppedBitmap(bitmap, new Int32Rect(8, 8, 8, 8)); head.Freeze(); return head;
    }
}
