using System.IO.Compression;
using System.Text.RegularExpressions;
using Launcher.Application.Diagnostics;
using Launcher.Application.Mods;

namespace Launcher.Application.Experience;

public sealed record InspectionIssue(string Title,string Evidence,string Advice,bool IsError=false)
{
    public string Level=>IsError?"需要处理":"建议检查";
}
public static partial class DiagnosticBundle
{
    public static string Redact(string value)
    {
        value=Regex.Replace(value,@"(?i)(Bearer\s+)[A-Za-z0-9._~+\-/=]+","$1<已隐藏>");
        value=Regex.Replace(value,@"(?i)(--(?:accessToken|refreshToken|clientToken|password)(?:=|\s+))(?:(""[^""]*"")|\S+)","$1<已隐藏>");
        value=Regex.Replace(value,"(?i)([\\\"]?(?:access_token|refresh_token|accessToken|refreshToken|clientToken|password)[\\\"]?\\s*[:=]\\s*[\\\"]?)[^\\\"\\s,}&]+","$1<已隐藏>");
        value=Regex.Replace(value,@"(?i)([A-Z]:\\(?:Users|360MoveData\\Users)\\)[^\\\r\n]+","$1<用户>");
        return value;
    }
    public static async Task ExportAsync(string destination,string gameRoot,string instanceId,string diagnosis,CancellationToken ct=default)
    {
        var game=ExperienceFiles.Child(Path.Combine(gameRoot,"instances"),instanceId);
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var stream=File.Create(temporary))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                await Write(zip,"诊断.txt",Redact(diagnosis),ct);
                var scan=InstalledModScanner.Scan(Path.Combine(game,".minecraft","mods"),ct);
                await Write(zip,"模组列表.txt",string.Join("\n",scan.Mods.Select(m=>m.Describe())),ct);
                var paths=new[]{Path.Combine(game,".minecraft","logs","latest.log")}.Concat(Directory.Exists(Path.Combine(gameRoot,"logs"))?Directory.EnumerateFiles(Path.Combine(gameRoot,"logs"),"*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3):[]);
                var index=0;
                foreach(var path in paths.Where(File.Exists))
                {
                    ct.ThrowIfCancellationRequested();await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                    input.Seek(-Math.Min(input.Length,131072),SeekOrigin.End);using var reader=new StreamReader(input);
                    await Write(zip,$"日志/{++index}.log",Redact(await reader.ReadToEndAsync(ct)),ct);
                }
            }
            File.Move(temporary,destination,true);
        }
        finally{File.Delete(temporary);}
    }
    private static async Task Write(ZipArchive zip,string name,string content,CancellationToken ct)
    {await using var stream=zip.CreateEntry(name).Open();await using var writer=new StreamWriter(stream);await writer.WriteAsync(content.AsMemory(),ct);}
}
