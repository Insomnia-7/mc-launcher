using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Launcher.Desktop.Controls;

/// <summary>Restart the Fluent indeterminate clock when a cached page is reattached.</summary>
public sealed class ActivityProgressBar : ProgressBar
{
    protected override Type StyleKeyOverride => typeof(ProgressBar);
    private int _attachment;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var attachment=++_attachment;
        Dispatcher.UIThread.Post(()=>
        {
            if(attachment!=_attachment||!IsIndeterminate)return;
            IsIndeterminate=false;
            IsIndeterminate=true;
        },DispatcherPriority.Loaded);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {++_attachment;base.OnDetachedFromVisualTree(e);}
}
