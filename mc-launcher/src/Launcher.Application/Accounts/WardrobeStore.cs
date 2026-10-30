using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Launcher.Application.Experience;

namespace Launcher.Application.Accounts;

public sealed record OwnedCape(string Id, string Name, string Url, bool Active);
public sealed record AppearanceProfile(string Id, string Name, string? SkinUrl, string Model, IReadOnlyList<OwnedCape> Capes);
public interface IPlayerAppearanceClient
{
    Task<AppearanceProfile> GetAsync(string token, CancellationToken ct = default);
    Task UploadSkinAsync(string token, byte[] png, string model, CancellationToken ct = default);
    Task ResetSkinAsync(string token, CancellationToken ct = default);
    Task SetCapeAsync(string token, string? capeId, CancellationToken ct = default);
    Task<byte[]> GetTextureAsync(string url, CancellationToken ct = default);
}
public sealed record WardrobeLook(string Id, string Name, string SkinFile, string Model, string? CapeFile, DateTimeOffset CreatedAt);
public sealed record WardrobeSelection(string LookId, bool UseInGame);

public sealed class WardrobeStore(string dataRoot, string identity)
{
    private readonly string _root = Path.Combine(dataRoot, "wardrobe", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))));
    public string Root => _root;
    public IReadOnlyList<WardrobeLook> List() => ExperienceFiles.Read<List<WardrobeLook>>(Path.Combine(_root, "looks.json")) ?? [];
    public WardrobeSelection? Selection => ExperienceFiles.Read<WardrobeSelection>(Path.Combine(_root, "selected.json"));
    public static void ValidatePng(byte[] data, bool cape = false)
    {
        if (data.Length is < 33 or > 2097152 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            throw new InvalidDataException("请选择有效的 PNG 图片（最大 2 MiB）。");
        var width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        if (cape ? width != 64 || height != 32 : width != 64 || height is not (32 or 64))
            throw new InvalidDataException(cape ? "披风需要 64×32 PNG。" : "皮肤需要 64×64 或经典 64×32 PNG。");
    }
    public WardrobeLook Import(string name, byte[] skin, string model, byte[]? cape = null)
    {
        ValidatePng(skin); if (cape is not null) ValidatePng(cape, true);
        if (model is not ("classic" or "slim")) throw new InvalidDataException("请选择宽手臂或细手臂模型。");
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        var skinFile = id + ".png"; File.WriteAllBytes(Path.Combine(_root, skinFile), skin);
        var capeFile = cape is null ? null : id + "-cape.png";
        if (capeFile is not null) File.WriteAllBytes(Path.Combine(_root, capeFile), cape!);
        var look = new WardrobeLook(id, string.IsNullOrWhiteSpace(name) ? "新皮肤" : name.Trim(), skinFile, model, capeFile, DateTimeOffset.UtcNow);
        ExperienceFiles.Write(Path.Combine(_root, "looks.json"), List().Append(look).ToArray());
        return look;
    }
    public byte[] Skin(WardrobeLook look) => File.ReadAllBytes(ExperienceFiles.Child(_root, look.SkinFile));
    public byte[]? Cape(WardrobeLook look) => look.CapeFile is null ? null : File.ReadAllBytes(ExperienceFiles.Child(_root, look.CapeFile));
    public void Select(WardrobeLook look, bool useInGame) => ExperienceFiles.Write(Path.Combine(_root, "selected.json"), new WardrobeSelection(look.Id, useInGame));
    public WardrobeLook SetModel(WardrobeLook look,string model)
    {
        if(model is not ("classic" or "slim"))throw new InvalidDataException("无效皮肤模型。");
        var updated=look with{Model=model};ExperienceFiles.Write(Path.Combine(_root,"looks.json"),List().Select(x=>x.Id==look.Id?updated:x).ToArray());return updated;
    }
    public void DisableInGame()
    {
        if (Selection is {} current) ExperienceFiles.Write(Path.Combine(_root, "selected.json"), current with {UseInGame = false});
    }
    public void Delete(WardrobeLook look)
    {
        ExperienceFiles.Write(Path.Combine(_root, "looks.json"), List().Where(x => x.Id != look.Id).ToArray());
        File.Delete(ExperienceFiles.Child(_root, look.SkinFile));
        if (look.CapeFile is {} cape) File.Delete(ExperienceFiles.Child(_root, cape));
        if (Selection?.LookId == look.Id) File.Delete(Path.Combine(_root, "selected.json"));
    }
    public void ApplyLocal(string gameDirectory, string playerName, WardrobeLook look)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(playerName, "^[A-Za-z0-9_]{3,16}$")) throw new InvalidDataException("离线用户名无效。");
        var root = Path.Combine(gameDirectory, "CustomSkinLoader", "LocalSkin", "Fangyu");
        ExperienceFiles.RejectLinks(root);
        Directory.CreateDirectory(Path.Combine(root, "skins"));
        Directory.CreateDirectory(Path.Combine(root, "capes"));
        File.WriteAllBytes(Path.Combine(root, "skins", playerName + ".png"), Skin(look));
        var capePath = Path.Combine(root, "capes", playerName + ".png");
        if (Cape(look) is {} cape) File.WriteAllBytes(capePath, cape); else File.Delete(capePath);
        var profile=new JsonObject
        {
            ["name"]="Fangyu",["type"]="Legacy",["model"]=look.Model=="slim"?"slim":"default",
            ["skin"]="LocalSkin/Fangyu/skins/{USERNAME}.png",["cape"]="LocalSkin/Fangyu/capes/{USERNAME}.png"
        };
        var config=Path.Combine(gameDirectory,"CustomSkinLoader","CustomSkinLoader.json");
        ExperienceFiles.RejectLinks(config);
        if(File.Exists(config))
        {
            var document=JsonNode.Parse(File.ReadAllText(config))?.AsObject()??throw new InvalidDataException("皮肤组件配置无法读取，请先检查配置文件。");
            var list=document["loadlist"] as JsonArray??throw new InvalidDataException("皮肤组件配置缺少 loadlist，请先恢复组件配置。");
            for(var i=list.Count-1;i>=0;i--)if(list[i]?["name"]?.GetValue<string>()=="Fangyu")list.RemoveAt(i);
            list.Insert(0,profile);ExperienceFiles.Write(config,document);
        }
        else
        {
            var extra=Path.Combine(gameDirectory,"CustomSkinLoader","ExtraList","Fangyu.json");ExperienceFiles.RejectLinks(extra);ExperienceFiles.Write(extra,profile);
        }
    }
    public static void RemoveLocalProfile(string gameDirectory,bool deleteTextures=false)
    {
        var root=Path.Combine(gameDirectory,"CustomSkinLoader");ExperienceFiles.RejectLinks(root);
        if(!Directory.Exists(root))return;
        var config=Path.Combine(root,"CustomSkinLoader.json");
        if(File.Exists(config))
        {
            var document=JsonNode.Parse(File.ReadAllText(config))?.AsObject()??throw new InvalidDataException("皮肤组件配置无法读取。");
            if(document["loadlist"] is JsonArray list)
            {
                for(var i=list.Count-1;i>=0;i--)if(list[i]?["name"]?.GetValue<string>()=="Fangyu")list.RemoveAt(i);
                ExperienceFiles.Write(config,document);
            }
        }
        File.Delete(Path.Combine(root,"ExtraList","Fangyu.json"));
        if(deleteTextures)ExperienceFiles.DeleteTree(Path.Combine(root,"LocalSkin","Fangyu"),root);
    }
}
