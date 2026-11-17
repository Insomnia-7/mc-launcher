using System.IO.Compression;
using Launcher.Application.Mods;

internal static class ModMetadataChecks
{
    public static void Run(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-mod-metadata-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Jar(string name,string json)
        {
            using var zip=ZipFile.Open(Path.Combine(root,name),ZipArchiveMode.Create);
            using var writer=new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());writer.Write(json);
        }
        try
        {
            Jar("provider.jar","{\"id\":\"entity_model_features\",\"version\":\"3.3.11\",\"description\":\"first line\nsecond line\"}");
            Jar("compat.jar","""{"id":"emf_compat_core","version":"2.3.0","depends":{"entity_model_features":">=3.3.11"}}""");
            var files=Directory.GetFiles(root);var issues=ModCompatibility.Inspect(files,"26.3","fabric","0.19.5");
            check("兼容 Fabric 描述中的实际换行，不再误报缺少 EMF 前置",!issues.Any(i=>i.IsError),null);
            File.Delete(Path.Combine(root,"compat.jar"));
            Jar("compat.jar","""{"id":"compat","version":"1","depends":{"entity_model_features":">=4.0"}}""");
            check("兼容元数据格式后仍检查真实版本冲突",ModCompatibility.Inspect(files,"26.3","fabric","0.19.5").Any(i=>i.IsError&&i.Title.Contains("版本不符")),null);
            File.Delete(Path.Combine(root,"provider.jar"));
            check("前置确实不存在时仍阻止启动",ModCompatibility.Inspect(Directory.GetFiles(root),"26.3","fabric","0.19.5").Any(i=>i.IsError&&i.Title.Contains("缺少前置")),null);
        }
        finally{Launcher.Application.Install.LongPath.DeleteDirectory(root);}
    }
}
