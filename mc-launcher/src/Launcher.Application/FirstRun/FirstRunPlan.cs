namespace Launcher.Application.FirstRun;

/// <summary>
/// 首次启动向导里的一个可选版本。界面只需要展示 <see cref="Title"/> 与 <see cref="Reason"/>，
/// 点下去时把 <see cref="VersionId"/> 交给安装流程。
/// </summary>
/// <param name="VersionId">Mojang 版本 id，例如 <c>1.21.4</c>。</param>
/// <param name="Title">标题，例如「Minecraft 1.21.4（最新正式版）」。</param>
/// <param name="Reason">一句话中文理由，直接显示给用户。</param>
/// <param name="EstimatedBytes">粗估的下载体积，见 <see cref="FirstRunService.EstimatedInstallBytes"/>。</param>
/// <param name="Recommended">是否推荐；一个计划里只有一个为 true（永远是第一个）。</param>
public sealed record FirstRunChoice(
    string VersionId,
    string Title,
    string Reason,
    long EstimatedBytes,
    bool Recommended)
{
    /// <summary>给界面直接用的 MB 数（粗估，保留一位小数足够）。</summary>
    public double EstimatedMegabytes => EstimatedBytes / 1024.0 / 1024.0;

    public override string ToString() => $"{Title} · {Reason}";
}

/// <summary>
/// 一次「首次启动」评估的结果。
/// <para>
/// <see cref="NeedsInstall"/> 为 false 时 <see cref="Choices"/> 是空列表——本机已经能直接启动，
/// 向导不该再推任何下载；为 true 时 <see cref="Choices"/> 按推荐顺序排列（第一个是推荐项）。
/// </para>
/// </summary>
/// <param name="Choices">候选版本，按推荐顺序。</param>
/// <param name="NeedsInstall">是否还需要先装一个版本才能玩。</param>
/// <param name="Headline">一句话结论，直接放在向导顶部。</param>
public sealed record FirstRunPlan(
    IReadOnlyList<FirstRunChoice> Choices,
    bool NeedsInstall,
    string Headline)
{
    /// <summary>推荐项（没有候选时为 null）。</summary>
    public FirstRunChoice? Recommended => Choices.FirstOrDefault(c => c.Recommended);

    /// <summary>是否一个候选都没有（例如清单拉取失败）。</summary>
    public bool IsEmpty => Choices.Count == 0;
}
