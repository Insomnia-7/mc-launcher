using System.IO.Compression;
using System.Text.Json;
using Launcher.Application.Mods;

internal static class RuntimeModChecks
{
    public static void Run(Action<string,bool,string?> check)
    {
        void Check(string name,bool okay)=>check(name,okay,null);
        var root=Path.Combine(Path.GetTempPath(),"fangyu-runtime-mods-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Json(ZipArchive archive,string path,object value){using var writer=new StreamWriter(archive.CreateEntry(path).Open());writer.Write(JsonSerializer.Serialize(value));}
        string Jar(string name,string id,string version,object? depends=null)
        {
            var path=Path.Combine(root,name);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            Json(zip,"fabric.mod.json",new{id,version,depends=depends??new{}});return path;
        }
        try
        {
            var lithium=Jar("lithium.jar","lithium","0.26.2",new{mixinextras=">=0.5.5"});
            var old=Jar("old.jar","mixinextras","0.5.4");
            var loader=Path.Combine(root,"libraries","net","fabricmc","fabric-loader","0.20.1","fabric-loader-0.20.1.jar");Directory.CreateDirectory(Path.GetDirectoryName(loader)!);
            using(var zip=ZipFile.Open(loader,ZipArchiveMode.Create))
            {
                Json(zip,"fabric.mod.json",new{id="fabricloader",version="0.20.1",jars=new[]{new{file="META-INF/jars/mixinextras.jar"}}});
                using var stream=zip.CreateEntry("META-INF/jars/mixinextras.jar").Open();using var inner=new ZipArchive(stream,ZipArchiveMode.Create);
                Json(inner,"fabric.mod.json",new{id="mixinextras",version="0.5.5"});
            }
            var version=Path.Combine(root,"versions","fabric","fabric.json");Directory.CreateDirectory(Path.GetDirectoryName(version)!);
            File.WriteAllText(version,"""{"id":"fabric","inheritsFrom":"26.3","libraries":[{"name":"net.fabricmc:fabric-loader:0.20.1"}]}""");
            var runtime=InstanceEnvironment.RuntimeModFiles(root,"fabric");
            Check("读取当前实例实际安装的 Fabric 运行库",runtime.SequenceEqual(new[]{loader}));
            Check("复现漏读加载器时 MixinExtras 版本误报",ModCompatibility.Inspect([lithium,old],"26.3","fabric","0.20.1").Any(i=>i.IsError));
            Check("加载器提供 0.5.5 时不被模组内旧版本误拦截",!ModCompatibility.Inspect([lithium,old],"26.3","fabric","0.20.1",runtimeFiles:runtime).Any(i=>i.IsError));
            Check("没有对应运行库仍报告真实前置版本问题",ModCompatibility.Inspect([lithium,old],"26.3","fabric","0.19.0").Any(i=>i.IsError&&i.Title.Contains("版本不符")));
            Check("正式版满足空预发布下界",ModCompatibility.Satisfies("26.3",">=26.3-")==true&&ModCompatibility.Satisfies("26.3","~26.3-")==true);
            Check("预发布及构建号可以精确比较",ModCompatibility.Satisfies("0.9.3-beta.1+mc26.3","=0.9.3-beta.1+mc26.3")==true);
            Check("预发布数字分段按数值比较",ModCompatibility.Satisfies("1.0-beta.10",">1.0-beta.2")==true);
            Check("正式版高于同版本预发布",ModCompatibility.Satisfies("1.0",">1.0-rc.1")==true&&ModCompatibility.Satisfies("1.0-rc.1",">=1.0")==false);
            Check("真实 Sodium 版本冲突仍被识别",ModCompatibility.Satisfies("0.9.3-beta.1+mc26.3","<=0.6.6")==false);
            Check("无法识别的版本继续作为未知处理",ModCompatibility.Satisfies("26w14a",">=26.3")==null);
        }
        finally{Launcher.Application.Install.LongPath.DeleteDirectory(root);}
    }
}
