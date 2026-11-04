namespace Launcher.Application.Install;

/// <summary>The instance owns its pack artwork; library refresh never needs a network request.</summary>
public static class ModpackIcon
{
    public const string FileName = ".fangyu-pack-icon";
    public const int MaxBytes = 2 * 1024 * 1024;

    public static bool IsImage(byte[]? bytes) => bytes is { Length: >= 12 and <= MaxBytes } &&
        (bytes.AsSpan().StartsWith(new byte[] {137,80,78,71,13,10,26,10}) ||
         bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ||
         bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8,4).SequenceEqual("WEBP"u8) ||
         bytes.AsSpan(0,6).SequenceEqual("GIF89a"u8) || bytes.AsSpan(0,6).SequenceEqual("GIF87a"u8));
}
