using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Launcher.App.ViewModels;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed partial class ToastItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ToastLevel Level { get; init; }
    public required string Message { get; init; }
    public string? ActionLabel { get; init; }
    public Action? ActionCallback { get; init; }
    public Action<ToastItem>? DismissRequested { get; init; }

    public bool HasAction => !string.IsNullOrEmpty(ActionLabel) && ActionCallback is not null;

    public string IconKind => Level switch
    {
        ToastLevel.Success => "StatusCheck",
        ToastLevel.Warning => "StatusWarning",
        ToastLevel.Error => "StatusError",
        _ => "StatusInfo"
    };

    public string AccentResourceKey => Level switch
    {
        ToastLevel.Success => "SuccessBrush",
        ToastLevel.Warning => "WarnBrush",
        ToastLevel.Error => "DangerBrush",
        _ => "AccentBrush"
    };

    public IBrush LevelBrush =>
        Avalonia.Application.Current?.Resources.TryGetResource(AccentResourceKey, null, out var v) == true && v is IBrush b
            ? b
            : Brushes.CornflowerBlue;

    [RelayCommand]
    private void InvokeAction()
    {
        DismissRequested?.Invoke(this);
        ActionCallback?.Invoke();
    }

    [RelayCommand]
    private void Close() => DismissRequested?.Invoke(this);
}

/// <summary>
/// 遵循 UI.md §15 Toast 规范：
/// - 从窗口右下方进入，多条通知有序堆叠排列（上限 4 条，超出自动淘汰最旧项）
/// - 支持 Success / Error / Warning / Info 四态（图标 + 颜色 + 文本）
/// - 支持手动关闭与定时自动消失
/// </summary>
public sealed class ToastManager
{
    public const int MaxVisibleToasts = 4;
    public ObservableCollection<ToastItem> Items { get; } = [];
    private readonly Dictionary<string, DispatcherTimer> _timers = new();

    public void Show(string message, ToastLevel level = ToastLevel.Info, string? actionLabel = null, Action? onAction = null, int durationMs = 5000)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message, level, actionLabel, onAction, durationMs));
            return;
        }

        while (Items.Count >= MaxVisibleToasts)
            Dismiss(Items[0]);

        var item = new ToastItem
        {
            Level = level,
            Message = message,
            ActionLabel = actionLabel,
            ActionCallback = onAction,
            DismissRequested = Dismiss
        };

        Items.Add(item);

        if (durationMs > 0)
        {
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(durationMs), DispatcherPriority.Background, (_, _) => Dismiss(item));
            _timers[item.Id] = timer;
            timer.Start();
        }
    }

    public void Dismiss(ToastItem item)
    {
        if (_timers.Remove(item.Id, out var timer))
            timer.Stop();
        Items.Remove(item);
    }
}
