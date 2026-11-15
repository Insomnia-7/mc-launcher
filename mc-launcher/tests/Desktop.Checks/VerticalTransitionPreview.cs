using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

/// <summary>Review-only film renderer. This class is in the test executable, never the launcher.</summary>
internal static class VerticalTransitionPreview
{
    public static async Task Record(MainViewModel model,MainWindow window,string root)
    {
        window.Width=1280;window.Height=800;model.IsSidebarCollapsed=false;
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");model.SelectThemeModeCommand.Execute("light");await Task.Delay(250);
        var host=window.GetVisualDescendants().OfType<AnimatedContentHost>().Single();
        var parent=(Panel)host.Parent!;
        RenderTargetBitmap Snapshot()
        {
            window.UpdateLayout();var bitmap=new RenderTargetBitmap(new PixelSize((int)host.Bounds.Width,(int)host.Bounds.Height),new Vector(96,96));bitmap.Render(host);return bitmap;
        }
        var actions=new Action[]{()=>model.ShowHomeCommand.Execute(null),()=>model.ShowInstancesCommand.Execute(null),()=>model.OpenMultiplayerCommand.Execute(null)};
        var pages=new List<RenderTargetBitmap>();
        foreach(var action in actions){action();await Task.Delay(220);pages.Add(Snapshot());}
        var stage=new Grid {ClipToBounds=true,Background=host.Background};
        parent.Children.Add(stage);host.IsVisible=false;
        var shell=window.FindControl<Grid>("Shell")!;
        var note=new TextBlock{Text="转场方案演示 · 300 ms · 用户已确认采用",FontSize=12,Foreground=Brush.Parse("#6554D9"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};Grid.SetColumnSpan(note,2);shell.Children.Add(note);
        var frame=0;
        void Capture()
        {
            window.UpdateLayout();using var image=new RenderTargetBitmap(new PixelSize(1280,800),new Vector(96,96));image.Render(window);image.Save(Path.Combine(root,$"frame-{frame++:D5}.png"),PngBitmapEncoderOptions.Default);
        }
        async Task Hold(int frames){for(var i=0;i<frames;i++){Capture();await Task.Delay(1);}}
        stage.Children.Add(new Image{Source=pages[0],Stretch=Stretch.Fill});actions[0]();await Task.Delay(80);await Hold(45);
        foreach(var pair in new[]{(0,1),(1,2),(2,1),(1,0)})
        {
            actions[pair.Item2]();await Task.Delay(80);
            var direction=pair.Item2>pair.Item1?1:-1;
            var outgoing=new Image{Source=pages[pair.Item1],Stretch=Stretch.Fill};var incoming=new Image{Source=pages[pair.Item2],Stretch=Stretch.Fill};
            var oldOffset=new TranslateTransform();var newOffset=new TranslateTransform();outgoing.RenderTransform=oldOffset;incoming.RenderTransform=newOffset;
            stage.Children.Clear();stage.Children.Add(outgoing);stage.Children.Add(incoming);
            const int steps=18;
            for(var i=0;i<=steps;i++)
            {
                // Deterministic 60fps samples of the proposed cubic ease-out; both pages stay opaque.
                var t=i/(double)steps;var eased=1-Math.Pow(1-t,3);var height=stage.Bounds.Height;
                oldOffset.Y=-direction*height*eased;newOffset.Y=direction*height*(1-eased);Capture();await Task.Delay(1);
            }
            stage.Children.Remove(outgoing);incoming.RenderTransform=null;await Hold(40);
        }
        parent.Children.Remove(stage);shell.Children.Remove(note);host.IsVisible=true;
        foreach(var bitmap in pages)bitmap.Dispose();
        File.WriteAllText(Path.Combine(root,"preview.txt"),$"Review-only vertical page slide, 300ms, 60fps, cubic ease-out, fully opaque pages, direction follows navigation order. {frame} frames. User approved 300ms; the matching production animation has separate native regression checks.");
        Console.WriteLine($"Recorded {frame} preview frames; approved 300ms design; native behavior is checked separately.");
    }
}
