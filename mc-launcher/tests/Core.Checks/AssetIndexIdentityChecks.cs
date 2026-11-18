using System.Net;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Meta;

internal static class AssetIndexIdentityChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-index-identity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"assets-34.json"),"""{"objects":{"old":{"hash":"0000000000000000000000000000000000000000","size":1}}}""");
            using var handler=new IndexHandler();using var http=new HttpClient(handler);var client=new MojangMetaClient(http,cacheDirectory:root);
            var old=new AssetIndexRef{Id="34",Sha1=new string('a',40),Url="https://example.test/old.json"};
            var current=new AssetIndexRef{Id="34",Sha1=new string('b',40),Url="https://example.test/new.json"};
            var a=await client.GetAssetIndexAsync(old);var b=await client.GetAssetIndexAsync(current);
            check("相同资源索引 ID 的不同哈希不复用旧内容",!a.Objects.ContainsKey("pack.mcmeta")&&b.Objects.ContainsKey("pack.mcmeta")&&handler.Calls==2,null);
            var disk=new MojangMetaClient(http,cacheDirectory:root);var restored=await disk.GetAssetIndexAsync(current);
            check("磁盘缓存也按精确资源哈希隔离",restored.Objects.ContainsKey("pack.mcmeta")&&handler.Calls==2,null);
            var next=new AssetIndexRef{Id="34",Url="https://example.test/new-url.json"};
            await client.GetAssetIndexAsync(next);await client.GetAssetIndexAsync(next);
            check("缺少哈希时按地址隔离并保留缓存复用",handler.Calls==3,null);
        }
        finally{Launcher.Application.Install.LongPath.DeleteDirectory(root);}
    }
    private sealed class IndexHandler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;var key=request.RequestUri!.AbsolutePath.Contains("new")?"pack.mcmeta":"sound.ogg";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"objects\":{\""+key+"\":{\"hash\":\"0000000000000000000000000000000000000000\",\"size\":1}}}")});
        }
    }
}
