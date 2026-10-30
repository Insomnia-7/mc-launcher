using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Experience;
using Launcher.Application.Update;
using Launcher.Infrastructure.Update;

internal static class LauncherUpdateChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fangyu-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Check(string name, bool value) => check(name, value, null);
        async Task<bool> Reject(Func<Task> operation) { try { await operation(); return false; } catch(Exception ex) when(ex is IOException or InvalidDataException or UpdateManifestException or InvalidOperationException or OperationCanceledException) { return true; } }
        try
        {
            using var rsa = RSA.Create(3072); var publicKey = rsa.ExportSubjectPublicKeyInfo(); var keyId = Convert.ToHexStringLower(SHA256.HashData(publicKey));
            var verifier = new UpdateSignatureVerifier(new Dictionary<string, string> { [keyId] = Convert.ToBase64String(publicKey) });
            var target = Path.Combine(root, "target"); var feed = Path.Combine(root, "feed"); var data = Path.Combine(root, "data"); Directory.CreateDirectory(target); Directory.CreateDirectory(feed);
            var payload = Encoding.UTF8.GetBytes("new launcher payload"); File.WriteAllBytes(Path.Combine(feed, "Fangyu.exe"), payload);
            void Sign(string fileName = "Fangyu.exe", long? size = null)
            {
                var raw = JsonSerializer.SerializeToUtf8Bytes(new { version = "0.7.0-preview.2", entry = fileName, notes = "测试更新", files = new[] { new { path = fileName, url = "Fangyu.exe", size = size ?? payload.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) } } });
                File.WriteAllBytes(Path.Combine(feed, "update.json"), raw);
                File.WriteAllText(Path.Combine(feed, "update.json.sig"), JsonSerializer.Serialize(new { schema = 1, algorithm = UpdateSignatureVerifier.Algorithm, keyId, signature = Convert.ToBase64String(rsa.SignData(raw, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) }));
            }
            Sign(); using var http = new HttpClient(); var client = new UpdateClient(http, verifier);
            var result = await client.CheckAsync("0.7.0-preview.1+abcdef", feed);
            Check("签名更新能识别后续预览版本", result.HasUpdate && result.Manifest?.Notes == "测试更新");
            Check("相同版本不同构建号不提示更新", !UpdateVersion.IsNewer("0.7.0-preview.1", "0.7.0-preview.1+abcdef") && !UpdateVersion.IsNewer("0.7.0-preview.1+123", "0.7.0-preview.1+456"));
            Check("补录旧版与预览版不会覆盖新版本", !UpdateVersion.IsNewer("0.3.0-preview.3", "0.7.0-preview.1") && !UpdateVersion.IsNewer("0.7.0-preview.1", "0.7.0"));
            var current = Path.Combine(target, "方隅.exe"); File.WriteAllText(current, "old launcher"); File.WriteAllText(Path.Combine(target, "accounts.json"), "keep account");
            var staged = await client.StageAsync(result.Manifest!, feed, target, data);
            var prepared = await SingleFileUpdate.PrepareAsync(staged.Directory, target, "方隅.exe", verifier);
            Check("签名载荷可映射到当前中文文件名", prepared.File.Path == "方隅.exe" && File.ReadAllBytes(Path.Combine(staged.Directory, "方隅.exe")).SequenceEqual(payload));
            var backup = await SingleFileUpdate.ApplyAsync(prepared);
            Check("替换仅修改启动器并保存旧版", File.ReadAllBytes(current).SequenceEqual(payload) && File.ReadAllText(Path.Combine(backup, "方隅.exe")) == "old launcher" && File.ReadAllText(Path.Combine(target, "accounts.json")) == "keep account" && !File.Exists(Path.Combine(target, "Fangyu.exe")));
            File.WriteAllText(current, "old launcher again");
            await File.WriteAllTextAsync(Path.Combine(staged.Directory, "Fangyu.exe"), "tampered");
            Check("暂存包篡改后禁止修改旧版", await Reject(() => SingleFileUpdate.PrepareAsync(staged.Directory, target, "方隅.exe", verifier)) && File.ReadAllText(current) == "old launcher again");
            Check("更新不能映射到目录外", await Reject(() => SingleFileUpdate.PrepareAsync(staged.Directory, target, "../outside.exe", verifier)));
            Sign("accounts.json"); var nonExe = await client.CheckAsync("0.6.0", feed);
            Check("单文件更新拒绝非 EXE 清单", await Reject(() => { SingleFileUpdate.ValidateManifest(nonExe.Manifest!); return Task.CompletedTask; }));
            Sign(); File.AppendAllText(Path.Combine(feed, "update.json"), " ");
            Check("篡改清单明确报告验证失败", (await client.CheckAsync("0.6.0", feed)) is { Failed: true, HasUpdate: false });
            Sign(size: payload.Length - 1); var oversized = await client.CheckAsync("0.6.0", feed);
            Check("超出大小的下载中止并清理半成品", await Reject(() => client.StageAsync(oversized.Manifest!, feed, target, Path.Combine(root, "oversized"))) && !Directory.EnumerateFiles(Path.Combine(root, "oversized"), "*.part", SearchOption.AllDirectories).Any());
            Sign(); var valid = (await client.CheckAsync("0.6.0", feed)).Manifest!;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Check("取消下载保留当前启动器", await Reject(() => client.StageAsync(valid, feed, target, data, ct: canceled.Token)) && File.ReadAllText(current) == "old launcher again");
            Check("未完整的更新器命令不会执行", await LauncherUpdateRunner.RunAsync(["--staging", feed, "--target"]) != 0);
        }
        finally { ExperienceFiles.DeleteTree(root, Path.GetTempPath()); }
    }
}
