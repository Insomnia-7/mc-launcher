using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Infrastructure.Horizon;

internal static class HorizonManifestChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-horizon-manifest-"+Guid.NewGuid().ToString("N"));
        void Check(string name,bool value)=>check(name,value,null);
        try
        {
            var buildDir=HorizonFixture.CreateBuild(Path.Combine(root,"build"));
            var original=Encoding.UTF8.GetBytes("{ \"id\": \"26.3\", \"unknown_mojang_field\": [1,2], \"javaVersion\": {\"majorVersion\":25} }\n");
            var hash=Convert.ToHexStringLower(SHA1.HashData(original));
            await File.WriteAllTextAsync(Path.Combine(buildDir,"research-lock.json"),JsonSerializer.Serialize(new{minecraft=new{version="26.3",java_major=25,manifest_sha1=hash}}));
            using var handler=new ManifestHandler(original);using var http=new HttpClient(handler);var client=new HorizonClient(http,root);
            var build=await client.InspectAsync(buildDir,CancellationToken.None);
            var game=Path.Combine(root,"game");var path=Path.Combine(game,"versions","26.3","26.3.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,"{\"id\":\"26.3\"}");
            var rejected=false;try{await client.PrepareAsync(build,game,"server.jar","java-home",null,CancellationToken.None);}catch(InvalidOperationException ex){rejected=ex.Message.Contains("Horizon knows");}
            Check("复现 Rust 拒绝被重新序列化的版本 JSON",rejected);
            var bytes=await client.OriginalVersionManifestAsync(build,game,CancellationToken.None);
            Check("Rust 使用构建锁定的 Mojang 原始包地址",handler.LastUrl==$"https://piston-meta.mojang.com/v1/packages/{hash}/26.3.json");
            Check("保留原始版本字节及未知字段",bytes.SequenceEqual(original));
            await File.WriteAllBytesAsync(path,bytes);await client.PrepareAsync(build,game,"server.jar","java-home",null,CancellationToken.None);
            Check("原始 JSON 通过真实子进程转换及完整性检查",(await client.CheckAsync(build,CancellationToken.None)).ExitCode==0);
            var calls=handler.Calls;await client.OriginalVersionManifestAsync(build,game,CancellationToken.None);
            Check("正确原始 JSON 可离线复用，不再次请求",handler.Calls==calls);
            await File.WriteAllTextAsync(path,"{}");handler.Bytes="tampered"u8.ToArray();rejected=false;
            try{await client.OriginalVersionManifestAsync(build,game,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
            Check("错误哈希在资源转换前拒绝且不覆盖旧文件",rejected&&await File.ReadAllTextAsync(path)=="{}");
        }
        finally{Launcher.Application.Install.LongPath.DeleteDirectory(root);}
    }
    private sealed class ManifestHandler(byte[] bytes):HttpMessageHandler
    {
        public byte[] Bytes=bytes;public int Calls;public string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;LastUrl=request.RequestUri!.ToString();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes)});}
    }
}
