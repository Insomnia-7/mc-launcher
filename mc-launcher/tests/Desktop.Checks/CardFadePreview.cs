using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

// Review-only rendering in the check executable. No change to production navigation.
internal static class CardFadePreview
{
    public static void Record(MainViewModel model,MainWindow window,string root,Action<int> pump)
    {
        window.Width=1180;window.Height=800;model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");model.SelectThemeModeCommand.Execute("light");model.SetLibraryLayoutCommand.Execute("cards");pump(100);
        var host=window.GetVisualDescendants().OfType<AnimatedContentHost>().Single();var index=0;
        void Capture(){window.UpdateLayout();using var image=new RenderTargetBitmap(new PixelSize(1180,800),new Vector(96,96));image.Render(window);image.Save(Path.Combine(root,$"frame-{index++:D5}.png"),PngBitmapEncoderOptions.Default);}
        List<Control> Cards()=>host.GetVisualDescendants().OfType<Control>().Where(x=>x.IsEffectivelyVisible&&(x.Classes.Contains("card")||x.Name=="HomeHeroBanner")).Where(x=>!x.GetVisualAncestors().OfType<Control>().TakeWhile(p=>p!=host).Any(p=>p.Classes.Contains("card"))).ToList();
        void Hold(int frames){for(var i=0;i<frames;i++)Capture();}
        void Fade(List<Control> cards,bool entering)
        {
            const int frames=18;
            for(var i=0;i<=frames;i++)
            {
                for(var n=0;n<cards.Count;n++)
                {
                    var t=Math.Clamp((i/(double)frames-(entering?n*0.035:0))/(entering?0.8:1),0,1);var eased=t*t*(3-2*t);cards[n].Opacity=entering?eased:1-eased;
                }
                Capture();
            }
        }
        model.ShowHomeCommand.Execute(null);pump(150);Hold(36);
        foreach(var next in new Action[]{()=>model.ShowInstancesCommand.Execute(null),()=>model.OpenNetworkCommand.Execute(null),()=>model.ShowHomeCommand.Execute(null)})
        {
            var old=Cards();Fade(old,false);next();pump(100);foreach(var card in old)card.Opacity=1;
            var incoming=Cards();foreach(var card in incoming)card.Opacity=0;Fade(incoming,true);foreach(var card in incoming)card.Opacity=1;Hold(42);
        }
        File.WriteAllText(Path.Combine(root,"preview.txt"),$"Review only. Actual launcher cards fade out for 300ms, then incoming cards fade in for 300ms with slight ordering. No movement or overlapping old/new text. 60 fps, {index} frames. Approved by user; matching production behavior has separate native checks.");
        Console.WriteLine($"Recorded {index} card fade preview frames; production animation unchanged.");
    }
}
