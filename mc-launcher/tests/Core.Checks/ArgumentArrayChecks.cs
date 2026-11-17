using System.Text;
using System.Text.Json;
using Launcher.Domain.Versions;
using Launcher.Domain.Launch;

internal static class ArgumentArrayChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        void Check(string name,bool value)=>check(name,value,null);
        const string json="""{"id":"array-test","arguments":{"game":["--name","${name}",{"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}","--height","${resolution_height}"]},"--end"],"jvm":[{"rules":[{"action":"allow"}],"value":["-Dfirst=true","-Dsecond=true"]}]}}""";
        var options=new JsonSerializerOptions{DefaultBufferSize=32,PropertyNameCaseInsensitive=true};
        await using var stream=new MemoryStream(Encoding.UTF8.GetBytes(json));
        var version=(await JsonSerializer.DeserializeAsync<VersionJson>(stream,options))!;
        Check("异步读取官方规则参数数组不会越界或丢后续参数",version.Arguments!.Game[2].Values?.Count==4&&version.Arguments.Game[3].Value=="--end");
        var vars=new Dictionary<string,string>{{"name","fixture"},{"resolution_width","1280"},{"resolution_height","720"}};
        var platform=new PlatformContext("windows","x86_64","10"){HasCustomResolution=true};
        Check("参数数组按顺序展开且保留每个独立参数",ArgumentExpander.ExpandAll(version.Arguments.Game,platform,vars).SequenceEqual(["--name","fixture","--width","1280","--height","720","--end"]));
        Check("数组参数仍服从功能规则",ArgumentExpander.ExpandAll(version.Arguments.Game,platform with{HasCustomResolution=false},vars).SequenceEqual(["--name","fixture","--end"]));
        Check("JVM 条件参数数组全部保留",ArgumentExpander.ExpandAll(version.Arguments.Jvm,platform,vars).SequenceEqual(["-Dfirst=true","-Dsecond=true"]));
        var saved=JsonSerializer.Serialize(version);var loaded=JsonSerializer.Deserialize<VersionJson>(saved)!;
        Check("加载器合并版本保存重读不会丢失参数数组",loaded.Arguments!.Game[2].Values!.SequenceEqual(version.Arguments.Game[2].Values!));
        var merged=VersionMerger.Merge(new VersionJson{Id="fabric",Arguments=new(){Game=["--child"]}},loaded);
        Check("加载器合并继续保留父版本条件数组",merged.Arguments!.Game[2].Values!.Count==4&&merged.Arguments.Game.Last().Value=="--child");
    }
}
