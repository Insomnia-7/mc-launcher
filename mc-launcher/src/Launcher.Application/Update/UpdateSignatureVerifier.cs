using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Application.Update;

public sealed record UpdateSignatureProof(string ManifestBase64, string SignatureJson);

/// <summary>验证发布者签名后才解析清单；信任来自随程序编译的公钥。</summary>
public sealed class UpdateSignatureVerifier
{
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxSignatureBytes = 16 * 1024;
    public const string Algorithm = "rsa-pss-sha256";
    private readonly IReadOnlyDictionary<string, string> _keys;
    public UpdateSignatureVerifier(IReadOnlyDictionary<string, string>? keys = null) =>
        _keys = new Dictionary<string, string>(keys ?? UpdateTrustStore.Keys, StringComparer.Ordinal);

    public UpdateManifest Verify(byte[] document, byte[] signature)
    {
        if (document.Length > MaxManifestBytes || signature.Length > MaxSignatureBytes)
            throw new UpdateManifestException("更新清单或签名超过大小限制。");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(signature);
            if (envelope is not { Schema: 1, Algorithm: Algorithm } || envelope.KeyId is null ||
                !_keys.TryGetValue(envelope.KeyId, out var publicKey))
                throw new UpdateManifestException("更新签名使用了未知公钥或不支持的格式，请从项目发布页获取可信版本。");
            using var rsa = RSA.Create();
            var spki = Convert.FromBase64String(publicKey);
            rsa.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || rsa.KeySize < 3072 ||
                Convert.ToHexStringLower(SHA256.HashData(spki)) != envelope.KeyId ||
                !rsa.VerifyData(document, Convert.FromBase64String(envelope.Signature ?? ""),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new UpdateManifestException("更新签名验证失败，已停止更新。请重新获取官方发布文件。");

            var json = new UTF8Encoding(false, true).GetString(document).TrimStart('\uFEFF');
            var manifest = UpdateManifestParser.Parse(json);
            // version 会用作暂存目录名；文件必须有可核验的完整摘要。
            if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Version, @"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,79}$") || manifest.Version.Contains(".."))
                throw new UpdateManifestException("更新版本号不能用作安全的暂存目录名。");
            foreach (var file in manifest.Files)
            {
                if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                    throw new UpdateManifestException("更新文件缺少有效的大小或 SHA-256 校验值。");
                if (file.Path.Equals("update.json", StringComparison.OrdinalIgnoreCase) ||
                    file.Path.Equals("update.json.sig", StringComparison.OrdinalIgnoreCase))
                    throw new UpdateManifestException("更新文件不能覆盖清单或签名文件。");
            }
            return manifest with { SignatureProof = new(Convert.ToBase64String(document), Encoding.UTF8.GetString(signature)) };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or DecoderFallbackException)
        {
            throw new UpdateManifestException("更新签名无效或文件已损坏，已停止更新。");
        }
    }

    public UpdateManifest Reverify(UpdateManifest manifest)
    {
        var proof = manifest.SignatureProof ?? throw new UpdateManifestException("更新清单缺少发布者签名，已停止更新。");
        byte[] raw;
        try { raw = Convert.FromBase64String(proof.ManifestBase64); }
        catch (FormatException) { throw new UpdateManifestException("更新清单签名凭据已损坏。"); }
        var verified = Verify(raw, Encoding.UTF8.GetBytes(proof.SignatureJson));
        if (JsonSerializer.Serialize(manifest) != JsonSerializer.Serialize(verified))
            throw new UpdateManifestException("更新清单在验证后发生变化，已停止更新。");
        return verified;
    }

    private sealed record Envelope(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("algorithm")] string? Algorithm,
        [property: JsonPropertyName("keyId")] string? KeyId,
        [property: JsonPropertyName("signature")] string? Signature);
}

public static class UpdateTrustStore
{
    public static IReadOnlyDictionary<string, string> Keys { get; } = Load();
    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(UpdateTrustStore).Assembly.GetManifestResourceStream("Launcher.UpdateTrustKeys.json")
            ?? throw new InvalidOperationException("更新公钥资源缺失。");
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []);
    }
}
