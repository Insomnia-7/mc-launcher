using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Domain.Install;
using Launcher.Domain.Versions;

internal sealed class MaintenanceFixture : IVersionMetaClient, IDownloader
{
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
    public readonly byte[] Client = Encoding.UTF8.GetBytes("valid client");
    public readonly byte[] Asset = Encoding.UTF8.GetBytes("valid asset");
    public List<DownloadEntry> Downloaded { get; } = [];
    public VersionJson Version => new()
    {
        Id = "maintenance", Downloads = new() { Client = new() { Url = "https://fixture/client", Size = Client.Length, Sha1 = Hash(Client) } },
        AssetIndex = new() { Id = "maintenance", Url = "https://fixture/index", Size = 2, Sha1 = Hash("{}"u8.ToArray()) }
    };
    public Task<VersionManifest> GetManifestAsync(CancellationToken ct = default) => Task.FromResult(new VersionManifest());
    public Task<VersionJson> GetVersionAsync(string versionId, CancellationToken ct = default) => Task.FromResult(Version);
    public Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference, CancellationToken ct = default) => Task.FromResult(new AssetIndex { Objects = new() { ["test"] = new() { Hash = Hash(Asset), Size = Asset.Length } } });
    public async Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries, string rootDirectory, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        long bytes = 0;
        foreach (var entry in entries)
        {
            var data = entry.Kind == DownloadKind.Client ? Client : entry.Kind == DownloadKind.AssetIndex ? "{}"u8.ToArray() : Asset;
            var path = ExperienceFiles.Child(rootDirectory, entry.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, data, ct); Downloaded.Add(entry); bytes += data.Length;
            progress?.Report(new(bytes, entries.Sum(e => e.Size), Downloaded.Count, entries.Count, entry.RelativePath));
        }
        return new(entries.Count, entries.Count, 0, bytes, TimeSpan.Zero);
    }
    public void SeedVersion(string game)
    {
        var directory = Path.Combine(game, "versions", Version.Id); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Version.Id + ".json"), JsonSerializer.Serialize(Version));
        File.WriteAllBytes(Path.Combine(directory, Version.Id + ".jar"), Client);
        var index = Path.Combine(game, "assets/indexes/maintenance.json"); Directory.CreateDirectory(Path.GetDirectoryName(index)!); File.WriteAllText(index, "{}");
    }
    public static string SeedWorld(string game, string instance, string folder, string name)
    {
        var directory = Path.Combine(game, "instances", instance, ".minecraft/saves", folder); Directory.CreateDirectory(directory);
        using (var output = File.Create(Path.Combine(directory, "level.dat")))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
        {
            void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text); writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((short)bytes.Length)); writer.Write(bytes); }
            void Tag(byte type, string key) { writer.Write(type); Text(key); }
            Tag(10, ""); Tag(10, "Data"); Tag(8, "LevelName"); Text(name);
            Tag(3, "GameType"); writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(1));
            Tag(4, "LastPlayed"); writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(1793419200000L));
            Tag(10, "Version"); Tag(8, "Name"); Text("1.21.4"); writer.Write((byte)0);
            writer.Write((byte)0); writer.Write((byte)0);
        }
        File.WriteAllText(Path.Combine(directory, "region.mca"), "original world"); File.WriteAllText(Path.Combine(directory, "session.lock"), "lock");
        return directory;
    }
}
