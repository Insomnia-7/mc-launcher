using System.Diagnostics;
using System.Text.Json;
using Launcher.Infrastructure.Horizon;

// A real child process that implements the documented wire protocol, not a game.
internal static class HorizonFixture
{
    public static bool IsFixture => Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "horizon-client" or "horizon-prepare";
    public static int Run(string[] args)
    {
        var modePath=Path.Combine(AppContext.BaseDirectory,"fixture-mode");
        var mode=File.Exists(modePath)?File.ReadAllText(modePath):"ok";
        string Value(string name)=>args[Array.IndexOf(args,name)+1];
        if(args.Contains("--launch-api"))
        {
            string[] options=["--resources","--game-dir","--home","--session-stdin","--prepare","--check","--output","--complete","--progress","--minecraft-dir","--server-jar","--java-home"];
            Console.WriteLine(JsonSerializer.Serialize(new {launch_api=mode=="api"?"9.0.0":"0.1.0",horizon="fixture",minecraft="26.3",java_major=25,options=options.Where(x=>mode!="option"||x!="--session-stdin").Select(name=>new{name})}));return 0;
        }
        if(args.Contains("--check"))return File.Exists(Path.Combine(Value("--output"),"ready"))?0:1;
        if(args.Contains("--prepare"))
        {
            using var research=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"research-lock.json")));
            if(research.RootElement.GetProperty("minecraft").TryGetProperty("manifest_sha1",out var sha1))
            {
                var manifest=Path.Combine(Value("--minecraft-dir"),"versions","26.3","26.3.json");
                if(!File.Exists(manifest)||Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(manifest)))!=sha1.GetString())
                {Console.Error.WriteLine("version JSON is not a version file Horizon knows");return 1;}
            }
            if(mode=="hang"){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"child.pid"),Environment.ProcessId.ToString());Thread.Sleep(30000);return 1;}
            var output=Value("--output");Directory.CreateDirectory(output);
            Console.WriteLine("{\"fraction\":0.5}");Console.WriteLine("{\"message\":\"fixture prepared\"}");
            if(mode=="fail"){Console.Error.WriteLine("fixture conversion failed");return 1;}
            File.WriteAllText(Path.Combine(output,"ready"),"complete");return 0;
        }
        using var session=JsonDocument.Parse(Console.In.ReadToEnd());
        var root=session.RootElement;
        if(root.GetProperty("user_type").GetString()!="msa"||root.GetProperty("name").GetString()!="FixturePlayer"||root.GetProperty("uuid").GetString()!="0123456789abcdef0123456789abcdef")return 2;
        var game=Value("--game-dir");
        File.WriteAllText(Path.Combine(game,"argv.json"),JsonSerializer.Serialize(args));
        File.WriteAllText(Path.Combine(game,"temp.txt"),Path.GetTempPath());
        Console.WriteLine("fixture session: "+root.GetProperty("access_token").GetString());
        Console.Error.WriteLine("fixture stderr");return mode=="crash"?101:0;
    }
    public static string CreateBuild(string root)
    {
        Directory.CreateDirectory(root);
        foreach(var file in Directory.EnumerateFiles(AppContext.BaseDirectory))File.Copy(file,Path.Combine(root,Path.GetFileName(file)),true);
        var apphost=Path.Combine(AppContext.BaseDirectory,Path.GetFileNameWithoutExtension(typeof(HorizonFixture).Assembly.Location)+".exe");
        File.Copy(apphost,Path.Combine(root,HorizonClient.ClientName),true);
        File.Copy(apphost,Path.Combine(root,HorizonClient.PreparerName),true);
        File.WriteAllText(Path.Combine(root,"research-lock.json"),"{}");Directory.CreateDirectory(Path.Combine(root,"exporters"));
        return root;
    }
}
