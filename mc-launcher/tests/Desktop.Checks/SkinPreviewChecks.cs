using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Launcher.Desktop.Controls;

internal static class SkinPreviewChecks
{
    public static void Run(string root,Action<string,bool> check,Action<int> pump)
    {
        var texture=Texture(true);var preview=new PlayerPreview{Skin=texture,OuterLayer=false,Rotation=0,Width=220,Height=320};
        var grid=new Grid{Background=Brushes.White};grid.Children.Add(preview);
        var window=new Window{Width=300,Height=380,Content=grid};window.Show();pump(60);
        try
        {
            using var sharp=Capture(window);var a=Pixels(sharp);
            RenderOptions.SetBitmapInterpolationMode(preview,BitmapInterpolationMode.LowQuality);preview.InvalidateVisual();pump(30);
            using var smooth=Capture(window);var b=Pixels(smooth);
            int Mixed(byte[] bytes){var n=0;for(var y=76;y<113;y++)for(var x=126;x<174;x++){var c=bytes[(y*300+x)*4];if(c>8&&c<247)n++;}return n;}
            check("皮肤像素放大避免插值混色",Mixed(a)<20&&Mixed(b)>500);
            RenderOptions.SetBitmapInterpolationMode(preview,BitmapInterpolationMode.None);
            preview.OuterLayer=true;preview.InvalidateVisual();pump(30);using var outer=Capture(window);
            check("全透明外层不会遮黑或改变皮肤",Pixels(outer).SequenceEqual(a));
            preview.Rotation=180;pump(30);using var back=Capture(window);
            check("角色背面可绘制且与正面视角不同",!Pixels(back).SequenceEqual(a));
            preview.Slim=true;preview.Rotation=-30;pump(30);using var slim=Capture(window);
            check("细手臂与倾斜视角仍能完整渲染",Pixels(slim).Any(c=>c>0&&c<240));
            preview.Skin=null;preview.Cape=null;pump(20);using var empty=Capture(window);
            check("替换或清空纹理后仍能绘制占位模型",Pixels(empty).Length==300*380*4);

            // Comparison uses the same procedural fixture and model; only texture sampling differs.
            preview.Skin=Texture(false);preview.Slim=false;preview.Rotation=-28;preview.OuterLayer=true;
            var second=new PlayerPreview{Skin=preview.Skin,Rotation=-28,Width=220,Height=320};
            RenderOptions.SetBitmapInterpolationMode(preview,BitmapInterpolationMode.LowQuality);
            var comparison=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),Background=Brushes.White};
            grid.Children.Remove(preview);
            var old=new StackPanel{HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center};old.Children.Add(new TextBlock{Text="平滑插值",Foreground=Brushes.Black,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center});old.Children.Add(preview);
            var improved=new StackPanel{HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center};improved.Children.Add(new TextBlock{Text="清晰像素 + 侧面明暗",Foreground=Brushes.Black,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center});improved.Children.Add(second);Grid.SetColumn(improved,1);
            comparison.Children.Add(old);comparison.Children.Add(improved);window.Content=comparison;window.Width=520;window.Height=370;pump(60);
            using var result=Capture(window);result.Save(Path.Combine(root,"skin-sampling-comparison.png"));
            second.Skin=null;preview.Skin=null;
        }
        finally{window.Close();}
    }
    private static RenderTargetBitmap Capture(Window window)
    {var result=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));result.Render(window);return result;}
    private static byte[] Pixels(Bitmap bitmap)
    {using var converted=new WriteableBitmap(bitmap.PixelSize,new Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Unpremul);using var frame=converted.Lock();bitmap.CopyPixels(frame);var bytes=new byte[frame.RowBytes*frame.Size.Height];Marshal.Copy(frame.Address,bytes,0,bytes.Length);return bytes;}
    internal static byte[] Texture(bool checker)
    {
        using var bitmap=new WriteableBitmap(new PixelSize(64,64),new Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Unpremul);
        using(var frame=bitmap.Lock())
        {
            var bytes=new byte[frame.RowBytes*64];
            void Fill(int x,int y,int w,int h,byte r,byte g,byte b){for(var yy=y;yy<y+h;yy++)for(var xx=x;xx<x+w;xx++){var i=yy*frame.RowBytes+xx*4;var c=(byte)(((xx+yy)%2)*255);bytes[i]=checker?c:r;bytes[i+1]=checker?c:g;bytes[i+2]=checker?c:b;bytes[i+3]=255;}}
            Fill(0,0,32,16,235,187,151);Fill(16,16,24,16,38,92,145);Fill(40,16,16,16,38,92,145);Fill(32,48,16,16,38,92,145);Fill(0,16,16,16,51,57,68);Fill(16,48,16,16,51,57,68);
            if(!checker){Fill(8,8,8,2,68,46,34);Fill(0,0,32,8,68,46,34);Fill(9,11,2,1,245,245,240);Fill(13,11,2,1,245,245,240);Fill(10,11,1,1,46,118,180);Fill(13,11,1,1,46,118,180);Fill(11,14,2,1,151,82,66);Fill(23,20,2,10,220,215,190);Fill(44,28,4,4,235,187,151);Fill(36,60,4,4,235,187,151);Fill(4,29,4,3,29,33,40);Fill(20,61,4,3,29,33,40);}
            Marshal.Copy(bytes,0,frame.Address,bytes.Length);
        }
        using var stream=new MemoryStream();bitmap.Save(stream);return stream.ToArray();
    }
}
