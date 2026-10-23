using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Update;
using Launcher.Infrastructure.Update;

internal static class UpdateSigningChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        void Check(string text, bool value) => check(text, value, null);
        using var key = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var id = Convert.ToHexStringLower(SHA256.HashData(publicKey));
        var verifier = new UpdateSignatureVerifier(new Dictionary<string,string> { [id] = Convert.ToBase64String(publicKey) });
        var bytes = Encoding.UTF8.GetBytes("signed fixture");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        byte[] Document(string version = "0.2.0", string? hash = null, string path = "app.bin") => JsonSerializer.SerializeToUtf8Bytes(new
        {
            version, notes = "测试签名", files = new[] { new { path, url = "app.bin", sha256 = hash ?? sha, size = bytes.Length } }
        });
        byte[] Sign(byte[] doc, RSA? signingKey = null, string? keyId = null, string algorithm = UpdateSignatureVerifier.Algorithm) =>
            JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, algorithm, keyId = keyId ?? id,
                signature = Convert.ToBase64String((signingKey ?? key).SignData(doc, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) });
        bool Reject(byte[] doc, byte[] signature)
        {
            try { verifier.Verify(doc, signature); return false; } catch (UpdateManifestException) { return true; }
        }
        var raw = Document(); var signed = Sign(raw);
        var manifest = verifier.Verify(raw, signed);
        Check("有效发布者签名可解析清单", manifest.Version == "0.2.0" && manifest.SignatureProof is not null);
        Check("有效签名可再次复核", verifier.Reverify(manifest).Files[0].Sha256 == sha);
        Check("修改版本号后签名失效", Reject(Document("0.3.0"), signed));
        Check("修改文件摘要后签名失效", Reject(Document(hash: new string('a',64)), signed));
        Check("相同语义但原始字节变化被拒绝", Reject(raw.Concat(new byte[] { 32 }).ToArray(), signed));
        Check("不信任签名中自带的未知公钥 ID", Reject(raw, Sign(raw, keyId: "unknown")));
        Check("其它私钥伪装可信公钥被拒绝", Reject(raw, Sign(raw, other)));
        Check("错误算法声明被拒绝", Reject(raw, Sign(raw, algorithm: "none")));
        Check("损坏签名被拒绝", Reject(raw, Encoding.UTF8.GetBytes("{bad")));
        Check("超大清单被拒绝", Reject(new byte[UpdateSignatureVerifier.MaxManifestBytes + 1], signed));
        Check("超大签名被拒绝", Reject(raw, new byte[UpdateSignatureVerifier.MaxSignatureBytes + 1]));
        var traversal = Document("../escape");
        Check("已签名的非法版本目录也被拒绝", Reject(traversal, Sign(traversal)));
        var noHash = Document(hash: "");
        Check("已签名文件仍必须有完整校验值", Reject(noHash, Sign(noHash)));
        var reserved = Document(path: "update.json.sig");
        Check("清单不能安装文件覆盖签名", Reject(reserved, Sign(reserved)));
        var root = Path.Combine(Path.GetTempPath(), "mcl-signature-" + Guid.NewGuid().ToString("N"));
        var oldEnvironment = Environment.GetEnvironmentVariable(UpdateClient.EnvironmentVariable);
        Environment.SetEnvironmentVariable(UpdateClient.EnvironmentVariable, null);
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target"); var data = Path.Combine(root, "data");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            await File.WriteAllBytesAsync(Path.Combine(source, "update.json"), raw);
            await File.WriteAllBytesAsync(Path.Combine(source, "update.json.sig"), signed);
            await File.WriteAllBytesAsync(Path.Combine(source, "app.bin"), bytes);
            using var handler = new FixtureHttp(raw, signed, bytes);
            using var http = new HttpClient(handler);
            var client = new UpdateClient(http, verifier);
            var found = await client.CheckAsync("0.1.0", source);
            Check("本地更新源必须通过签名检查", found.HasUpdate);
            var stage = await client.StageAsync(found.Manifest!, source, target, data);
            Check("暂存完整复制签名对应的原始清单", File.ReadAllBytes(Path.Combine(stage.Directory,"update.json")).SequenceEqual(raw));
            Check("暂存签名可供独立更新器验证", verifier.Verify(File.ReadAllBytes(Path.Combine(stage.Directory,"update.json")), File.ReadAllBytes(Path.Combine(stage.Directory,"update.json.sig"))).Version == "0.2.0");
            Check("签名验证后下载文件仍校验 SHA256", File.ReadAllBytes(Path.Combine(stage.Directory,"app.bin")).SequenceEqual(bytes));
            var changed = false;
            try { await client.StageAsync(manifest with { Version = "0.9.0" }, source, target, data); }
            catch (UpdateManifestException) { changed = true; }
            Check("内存清单被修改时在创建暂存目录前拒绝", changed && !Directory.Exists(Path.Combine(data,"update","0.9.0")));
            var unsigned = false;
            try { await client.StageAsync(manifest with { SignatureProof = null }, source, target, data); }
            catch (UpdateManifestException) { unsigned = true; }
            Check("直接调用暂存也不能绕过签名", unsigned);
            File.Delete(Path.Combine(source,"update.json.sig"));
            Check("旧版无签名清单明确拒绝", !(await client.CheckAsync("0.1.0",source)).HasUpdate);
            Check("HTTPS 更新源读取并验证签名", (await client.CheckAsync("0.1.0","https://fixture.invalid/update.json?channel=stable")).HasUpdate);
            Check("签名地址保留清单查询参数", handler.Requests.Contains("/update.json.sig?channel=stable"));
            handler.Signature = Sign(raw, other);
            var bad = await client.CheckAsync("0.1.0","https://fixture.invalid/update.json");
            Check("远端伪造清单明确报验证失败", !bad.HasUpdate && bad.Reason!.StartsWith("更新验证失败"));
            Check("验证失败不请求发布文件", !handler.Requests.Contains("/app.bin"));
            handler.Signature = signed;
            Check("已验证的相同版本不触发更新", !(await client.CheckAsync("0.2.0","https://fixture.invalid/update.json")).HasUpdate);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var didCancel = false;
            try { await client.CheckAsync("0.1.0","https://fixture.invalid/update.json",cancelled.Token); }
            catch (OperationCanceledException) { didCancel = true; }
            Check("签名读取取消保留取消语义", didCancel);
            var otherPublic = other.ExportSubjectPublicKeyInfo();
            var otherId = Convert.ToHexStringLower(SHA256.HashData(otherPublic));
            var rotated = new UpdateSignatureVerifier(new Dictionary<string,string> { [id] = Convert.ToBase64String(publicKey), [otherId] = Convert.ToBase64String(otherPublic) });
            Check("轮换过渡版本可同时验证新旧公钥", rotated.Verify(raw,signed).Version == "0.2.0" && rotated.Verify(raw,Sign(raw,other,otherId)).Version == "0.2.0");
            Check("生产公钥目录已内置且只有公开材料", UpdateTrustStore.Keys.Count > 0 && UpdateTrustStore.Keys.All(pair => !pair.Value.Contains("PRIVATE")));
        }
        finally { Environment.SetEnvironmentVariable(UpdateClient.EnvironmentVariable,oldEnvironment); Directory.Delete(root,true); }
    }
    private sealed class FixtureHttp(byte[] document, byte[] signature, byte[] payload) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public byte[] Signature = signature;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.PathAndQuery);
            var bytes = request.RequestUri.AbsolutePath.EndsWith(".sig") ? Signature : request.RequestUri.AbsolutePath.EndsWith(".json") ? document : payload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
