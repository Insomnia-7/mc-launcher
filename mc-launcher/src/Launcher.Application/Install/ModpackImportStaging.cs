using Launcher.Application.Mods;
using Launcher.Domain.Install;

namespace Launcher.Application.Install;

/// <summary>Prepare privately; journal only the files replaced at commit so cancellation preserves an existing instance.</summary>
internal sealed class ModpackImportStaging : IDisposable
{
    private readonly string _target;
    private readonly List<(string Target,string? Backup)> _changes=[];
    private readonly List<string> _createdDirectories=[];
    private bool _movedNewInstance, _complete;
    public string Root {get;}
    public string InstanceRoot {get;}
    public string GameDirectory=>Path.Combine(InstanceRoot,".minecraft");
    public List<string> CleanupFailures {get;}=[];

    public ModpackImportStaging(string gameRoot,string instanceId)
    {
        var root=Path.GetFullPath(gameRoot);
        // Instance IDs are allocated by the registry. Keep the private staging tree under this game root.
        _target=LongPath.Combine(Path.Combine(root,"instances"),instanceId);
        if(!Path.GetFullPath(_target).StartsWith(Path.Combine(root,"instances")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("实例导入路径越界");
        Root=Path.Combine(root,".cache","imports",Guid.NewGuid().ToString("N"));
        InstanceRoot=Path.Combine(Root,"instances",instanceId);
        LongPath.EnsureDirectory(GameDirectory);
    }

    public async Task ReuseExistingAsync(IReadOnlyList<DownloadEntry> files,CancellationToken ct)
    {
        foreach(var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var original=LongPath.Normalize(LongPath.Combine(Path.Combine(_target,".minecraft"),file.RelativePath));
            if(!File.Exists(original)||file.Size>0&&new FileInfo(original).Length!=file.Size) continue;
            if(file.Sha1 is {Length:>0} hash&&!string.Equals(await ModSourceStore.HashAsync(original,ct).ConfigureAwait(false),hash,StringComparison.OrdinalIgnoreCase)) continue;
            var staged=LongPath.Normalize(LongPath.Combine(GameDirectory,file.RelativePath));
            LongPath.EnsureDirectory(Path.GetDirectoryName(staged)!);
            await using var source=File.OpenRead(original);
            await using var target=LongPath.CreateFile(staged);
            await source.CopyToAsync(target,ct).ConfigureAwait(false);
        }
    }

    public void CopyOriginIndex(string stagedDirectory)
    {
        var relative=Path.GetRelativePath(GameDirectory,stagedDirectory);
        var original=Path.Combine(LongPath.Combine(Path.Combine(_target,".minecraft"),relative=="."?ModSourceStore.FileName:relative+Path.DirectorySeparatorChar+ModSourceStore.FileName));
        if(!File.Exists(LongPath.Normalize(original))) return;
        LongPath.EnsureDirectory(stagedDirectory);
        File.Copy(LongPath.Normalize(original),LongPath.Normalize(Path.Combine(stagedDirectory,ModSourceStore.FileName)),true);
    }

    public void Apply(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RejectLinks(_target);
        if(!Directory.Exists(LongPath.Normalize(_target)))
        {
            LongPath.EnsureDirectory(Path.GetDirectoryName(_target)!);
            Directory.Move(LongPath.Normalize(InstanceRoot),LongPath.Normalize(_target));
            _movedNewInstance=true;
            return;
        }
        foreach(var staged in Directory.EnumerateFiles(LongPath.Normalize(InstanceRoot),"*",SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative=Path.GetRelativePath(LongPath.Normalize(InstanceRoot),staged);
            var target=LongPath.Normalize(LongPath.Combine(_target,relative));
            RejectLinks(target);
            EnsureTargetDirectory(Path.GetDirectoryName(target)!);
            string? backup=null;
            if(File.Exists(target))
            {
                backup=LongPath.Normalize(LongPath.Combine(Path.Combine(Root,"backup"),relative));
                LongPath.EnsureDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target,backup);
            }
            _changes.Add((target,backup));
            File.Move(staged,target);
        }
        ct.ThrowIfCancellationRequested();
    }

    private void EnsureTargetDirectory(string path)
    {
        var missing=new Stack<string>();
        for(var current=path;!Directory.Exists(current);current=Path.GetDirectoryName(current)!) missing.Push(current);
        while(missing.TryPop(out var current)) {Directory.CreateDirectory(current);_createdDirectories.Add(current);}
    }
    private static void RejectLinks(string path)
    {
        for(var current=path;current is not null;current=Path.GetDirectoryName(current))
            if((Directory.Exists(current)||File.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("导入目标包含目录或文件链接，请先检查："+current);
    }

    public void Complete() { _complete=true;_changes.Clear(); }
    public void Rollback()
    {
        if(_complete) return;
        CleanupFailures.Clear();
        for(var i=_changes.Count-1;i>=0;i--)
        {
            var change=_changes[i];
            try
            {
                if(change.Backup is null) File.Delete(change.Target);
                else File.Move(change.Backup,change.Target,true);
                _changes.RemoveAt(i);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {CleanupFailures.Add(change.Target);}
        }
        if(_movedNewInstance)
        {
            try {LongPath.DeleteDirectory(_target);_movedNewInstance=false;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {CleanupFailures.Add(_target);}
        }
        foreach(var path in _createdDirectories.AsEnumerable().Reverse())
            try {if(Directory.Exists(path)&&!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {CleanupFailures.Add(path);}
    }
    public void Cleanup()
    {
        // Preserve the backup if a locked original could not be restored.
        if(_changes.Count>0) {CleanupFailures.Add(Root);return;}
        try {LongPath.DeleteDirectory(Root);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {CleanupFailures.Add(Root);}
    }
    public void Dispose() {if(!_complete) Rollback();Cleanup();}
}

public sealed class ModpackImportCanceledException(IReadOnlyList<string> remainingPaths,CancellationToken ct)
    : OperationCanceledException(remainingPaths.Count==0
        ?"已取消导入，未完成的整合包文件已清理；原有实例和已完成的共享依赖已保留。"
        :"已取消导入，但部分文件被占用或无权清理，请关闭占用程序后检查："+string.Join("；",remainingPaths),ct)
{
    public IReadOnlyList<string> RemainingPaths {get;}=remainingPaths;
}
