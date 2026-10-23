using System.Text;

namespace Launcher.Application.Install;

/// <summary>
/// 导入失败 / 部分成功时没拿到手的那个文件。
/// 界面拿它填状态栏，进度回调拿它写 Detail 行，CLI 拿它拼 <c>[失败]</c> 文本 —— 三处共用一份数据。
/// </summary>
public sealed record MissingModpackFile(string RelativePath, string? Url, long Size, string Reason)
{
    public double Megabytes => Size / 1024.0 / 1024.0;

    public string Describe() =>
        Url is { Length: > 0 }
            ? $"{RelativePath}（{Megabytes:F1} MB）← {Url} · {Reason}"
            : $"{RelativePath}（{Megabytes:F1} MB）· {Reason}";

    public override string ToString() => Describe();

    /// <summary>一行摘要，给进度 Detail / 状态栏用；只列前 <paramref name="max"/> 个，避免撑爆一行。</summary>
    public static string Summarize(IReadOnlyList<MissingModpackFile> files, int max = 3)
    {
        if (files.Count == 0) return "没有缺失文件";

        var head = string.Join("、", files.Take(max).Select(f => f.RelativePath));
        return files.Count > max
            ? $"{files.Count} 个文件待补全：{head} 等"
            : $"{files.Count} 个文件待补全：{head}";
    }
}

/// <summary>
/// 整合包导入失败。<see cref="Exception.Message"/> 已经是能直接给人看的多行文本
/// （原因 + 回滚了什么 + 缺哪些文件），CLI 的 <c>[失败]</c> 和界面的错误框都直接用它。
/// </summary>
public sealed class ModpackImportFailedException : Exception
{
    public ModpackImportFailedException(
        string message,
        IReadOnlyList<MissingModpackFile> missingFiles,
        IReadOnlyList<string> removedPaths,
        Exception? innerException = null)
        : base(message, innerException)
    {
        MissingFiles = missingFiles;
        RemovedPaths = removedPaths;
    }

    /// <summary>没下到的文件清单（空 = 失败与下载无关，比如加载器安装挂了）。</summary>
    public IReadOnlyList<MissingModpackFile> MissingFiles { get; }

    /// <summary>回滚时真正删掉的目录（空 = 没有本次新建的东西，或回滚失败）。</summary>
    public IReadOnlyList<string> RemovedPaths { get; }

    public bool RolledBack => RemovedPaths.Count > 0;

    /// <summary>把底层异常包成带「回滚了什么 / 缺哪些文件」的失败。</summary>
    public static ModpackImportFailedException From(
        string packName,
        Exception cause,
        IReadOnlyList<MissingModpackFile> missingFiles,
        IReadOnlyList<string> removedPaths)
    {
        const int maxListed = 8;

        var message = new StringBuilder();
        message.Append($"整合包「{packName}」导入失败：{Flatten(cause)}");

        if (removedPaths.Count > 0)
        {
            message.Append($"{Environment.NewLine}已回滚本次新建的 {removedPaths.Count} 项：");
            message.Append(string.Join("、", removedPaths));
        }

        if (missingFiles.Count > 0)
        {
            message.Append($"{Environment.NewLine}待补全 {missingFiles.Count} 个文件：");
            foreach (var file in missingFiles.Take(maxListed))
                message.Append($"{Environment.NewLine}  · {file.Describe()}");

            if (missingFiles.Count > maxListed)
                message.Append($"{Environment.NewLine}  · … 另有 {missingFiles.Count - maxListed} 个");
        }

        return new ModpackImportFailedException(message.ToString(), missingFiles, removedPaths, cause);
    }

    /// <summary>下载器会把一批失败包成 AggregateException，多行堆栈对用户没价值，摊平成一行。</summary>
    private static string Flatten(Exception ex) => ex switch
    {
        AggregateException aggregate => string.Join(
            "; ",
            aggregate.Flatten().InnerExceptions.Select(Flatten).Distinct(StringComparer.Ordinal)),
        IOException { InnerException: { } inner } io => $"{io.Message} ← {inner.Message}",
        _ => ex.Message,
    };
}
