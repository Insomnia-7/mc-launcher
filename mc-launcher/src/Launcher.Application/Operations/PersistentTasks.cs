using System.Collections.Concurrent;
using Launcher.Application.Diagnostics;
using Launcher.Application.Experience;

namespace Launcher.Application.Operations;

public enum DurableTaskState { Ready, Running, Paused, Completed, Failed, Canceled }
public sealed record DurableTaskRequest(string Kind,string Title,string GameRoot,string? InstanceId,Dictionary<string,string> Arguments);
public sealed record DurableTaskRecord(string Id,DurableTaskRequest Request,DurableTaskState State,double Percent,string Detail,DateTimeOffset CreatedAt,IReadOnlyList<string> Partials)
{
    public string Title=>Request.Title;
    public string StateText=>State switch {DurableTaskState.Ready=>"待开始",DurableTaskState.Running=>"进行中",DurableTaskState.Paused=>"已暂停，可恢复",DurableTaskState.Completed=>"已完成",DurableTaskState.Failed=>"失败，可重试",_=>"已取消"};
    public bool CanResume=>State is DurableTaskState.Ready or DurableTaskState.Paused or DurableTaskState.Failed;
    public bool CanPause=>State==DurableTaskState.Running;
    public bool CanCancel=>State is not (DurableTaskState.Completed or DurableTaskState.Canceled);
}
public sealed record TaskProgress(double Percent,string Detail);

/// <summary>Flows through asynchronous installers, including parallel downloads. No account tokens are persisted.</summary>
public sealed class DurableExecution
{
    private static readonly AsyncLocal<DurableExecution?> Context=new();
    public static DurableExecution? Current {get=>Context.Value;internal set=>Context.Value=value;}
    public string Directory {get;}
    public bool Pausing {get;internal set;}
    public ConcurrentDictionary<string,byte> Partials {get;}=new(StringComparer.OrdinalIgnoreCase);
    internal DurableExecution(string directory)=>Directory=directory;
    public static void TrackPartial(string path)=>Current?.Partials.TryAdd(Path.GetFullPath(path),0);
}

public sealed class PersistentTasks
{
    private readonly string _root;
    private readonly object _sync=new();
    private readonly List<DurableTaskRecord> _tasks=[];
    private CancellationTokenSource? _running;
    private DurableExecution? _execution;
    private string? _active;
    private TaskCompletionSource? _completion;
    public Func<DurableTaskRequest,IProgress<TaskProgress>,CancellationToken,Task>? Execute {get;set;}
    public event Action? Changed;
    public string? ActiveId {get{lock(_sync)return _active;}}
    public IReadOnlyList<DurableTaskRecord> List() {lock(_sync)return _tasks.OrderByDescending(t=>t.CreatedAt).ToArray();}
    public PersistentTasks(string dataDirectory)
    {
        _root=Path.Combine(dataDirectory,"task-center");
        if(!System.IO.Directory.Exists(_root))return;
        foreach(var file in System.IO.Directory.EnumerateFiles(_root,"*.json"))
        {
            var record=ExperienceFiles.Read<DurableTaskRecord>(file);
            if(record is null||!Guid.TryParseExact(record.Id,"N",out _))continue;
            if(record.State==DurableTaskState.Running)record=record with{State=DurableTaskState.Paused,Detail="上次运行中断，点击恢复继续校验和安装。"};
            _tasks.Add(record);
        }
    }
    public DurableTaskRecord Add(DurableTaskRequest request)
    {
        var item=new DurableTaskRecord(Guid.NewGuid().ToString("N"),request with{GameRoot=Path.GetFullPath(request.GameRoot),Arguments=new(request.Arguments)},DurableTaskState.Ready,0,"等待开始",DateTimeOffset.UtcNow,[]);
        lock(_sync){_tasks.Add(item);Save(item);}Changed?.Invoke();return item;
    }
    private void Save(DurableTaskRecord record)=>ExperienceFiles.Write(ExperienceFiles.Child(_root,record.Id+".json"),record);
    private void Update(string id,Func<DurableTaskRecord,DurableTaskRecord> update)
    {
        lock(_sync){var index=_tasks.FindIndex(x=>x.Id==id);if(index<0)return;_tasks[index]=update(_tasks[index]);Save(_tasks[index]);}Changed?.Invoke();
    }
    public async Task RunAsync(string id,CancellationToken ct=default)
    {
        DurableTaskRecord item;DurableExecution execution;CancellationTokenSource cancellation;
        lock(_sync)
        {
            if(_active is not null)throw new InvalidOperationException("请先暂停或完成当前任务。");
            item=_tasks.Single(x=>x.Id==id);if(!item.CanResume)throw new InvalidOperationException("该任务无需恢复。");
            _active=id;_running=cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
            _completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
            _execution=execution=new(ExperienceFiles.Child(_root,id));
            foreach(var path in item.Partials)execution.Partials.TryAdd(path,0);
        }
        var previous=DurableExecution.Current;DurableExecution.Current=execution;
        Update(id,t=>t with{State=DurableTaskState.Running,Detail="正在校验已完成文件…"});
        var last=DateTimeOffset.MinValue;
        try
        {
            var handler=Execute??throw new InvalidOperationException("任务处理器尚未就绪。");
            await handler(item.Request,new DirectProgress<TaskProgress>(p=>
            {
                if(cancellation.IsCancellationRequested)return;
                var now=DateTimeOffset.UtcNow;if(now-last<TimeSpan.FromMilliseconds(250)&&p.Percent<100)return;
                last=now;Update(id,t=>t.State==DurableTaskState.Running?t with{Percent=Math.Clamp(p.Percent,0,100),Detail=p.Detail,Partials=execution.Partials.Keys.ToArray()}:t);
            }),cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            Update(id,t=>t with{State=DurableTaskState.Completed,Percent=100,Detail="安装完成",Partials=[]});
            ExperienceFiles.DeleteTree(execution.Directory,_root);
        }
        catch(OperationCanceledException)
        {
            Update(id,t=>t with{State=execution.Pausing?DurableTaskState.Paused:DurableTaskState.Canceled,Detail=execution.Pausing?"断点与安装清单已保存；可恢复。":"任务已取消，正在清理未完成文件。",Partials=execution.Partials.Keys.ToArray()});
            if(!execution.Pausing)Cleanup(id);
        }
        catch(Exception ex)
        {Update(id,t=>t with{State=DurableTaskState.Failed,Detail=ErrorText.Describe(ex),Partials=execution.Partials.Keys.ToArray()});}
        finally
        {
            DurableExecution.Current=previous;
            lock(_sync){_active=null;_execution=null;_running=null;_completion?.TrySetResult();}
            cancellation.Dispose();Changed?.Invoke();
        }
    }
    public void Pause(string id)
    {
        lock(_sync)if(_active==id&&_execution is not null){_execution.Pausing=true;_running?.Cancel();}
    }
    public void Cancel(string id)
    {
        lock(_sync)if(_active==id){if(_execution is not null)_execution.Pausing=false;_running?.Cancel();return;}
        Update(id,t=>t with{State=DurableTaskState.Canceled});Cleanup(id);
    }
    private void Cleanup(string id)
    {
        var record=List().Single(t=>t.Id==id);var failures=new List<string>();
        var allowedRoots=new[]{record.Request.GameRoot,ExperienceFiles.Child(_root,id),Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_root))!,"java",".cache")};
        foreach(var path in record.Partials)
        {
            try
            {
                var full=Path.GetFullPath(path);
                var owner=allowedRoots.FirstOrDefault(root=>full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal));
                if(!path.EndsWith(".part",StringComparison.OrdinalIgnoreCase)||owner is null)continue;
                ExperienceFiles.Child(owner,Path.GetRelativePath(owner,full));
                ExperienceFiles.RejectLinks(path);File.Delete(path);
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidDataException){failures.Add(path);}
        }
        try{ExperienceFiles.DeleteTree(ExperienceFiles.Child(_root,id),_root);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failures.Add(id);}
        Update(id,t=>t with{Detail=failures.Count==0?"已取消，未完成文件已清理；完整共享资源保留。":"部分文件无法清理，请检查："+string.Join("；",failures),Partials=failures});
    }
    public void PauseActiveForShutdown() {if(ActiveId is {} id)Pause(id);}
    public async Task PauseAndWaitAsync()
    {
        Task? completion;
        lock(_sync){if(_execution is not null)_execution.Pausing=true;_running?.Cancel();completion=_completion?.Task;}
        if(completion is not null)await completion.ConfigureAwait(false);
    }
    private sealed class DirectProgress<T>(Action<T> report):IProgress<T>{public void Report(T value)=>report(value);}
}
