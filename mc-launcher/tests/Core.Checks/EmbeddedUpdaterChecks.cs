using Launcher.Infrastructure.Update;

internal static class EmbeddedUpdaterChecks
{
    public static void Run(Action<string, bool, string?> check)
    {
        void Check(string name, bool value) => check(name, value, null);
        var root = Path.Combine(Path.GetTempPath(), "mcl-embedded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] payload = [1,2,3,4,5,6];
            string Extract(byte[] bytes) => EmbeddedUpdater.Extract(new MemoryStream(bytes), root);
            var path = Extract(payload);
            Check("内嵌更新器完整提取到缓存", File.ReadAllBytes(path).SequenceEqual(payload) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar));
            var stamp = File.GetLastWriteTimeUtc(path);
            Check("相同内嵌更新器可复用且不重写", Extract(payload) == path && File.GetLastWriteTimeUtc(path) == stamp);
            File.WriteAllBytes(path, [6,5,4,3,2,1]);
            Check("同长度损坏缓存也会被修复", Extract(payload) == path && File.ReadAllBytes(path).SequenceEqual(payload));
            var changed = Extract([6,5,4,3,2,1]);
            Check("不同内容使用独立缓存不覆盖旧更新器", changed != path && File.ReadAllBytes(path).SequenceEqual(payload));
            var empty = false;
            try { Extract([]); } catch (InvalidDataException) { empty = true; }
            Check("空内嵌资源被明确拒绝", empty);
            Check("提取完成不留下半成品", Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0);
            var occupied = false;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { Extract(payload); } catch (IOException) { occupied = true; }
            }
            Check("缓存占用失败可重试", occupied && Extract(payload) == path);
            var blockedRoot = Path.Combine(root,"file"); File.WriteAllText(blockedRoot,"keep");
            var blocked = false;
            try { EmbeddedUpdater.Extract(new MemoryStream(payload), blockedRoot); } catch (IOException) { blocked = true; }
            Check("不可写缓存失败保留原文件", blocked && File.ReadAllText(blockedRoot) == "keep");
        }
        finally { Directory.Delete(root,true); }
    }
}
