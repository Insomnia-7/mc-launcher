using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

internal sealed class GpuTestApplication:Avalonia.Application
{
    public static string Root="";
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var preview=new PlayerPreview{Skin=SkinPreviewChecks.Texture(false),Cape=GpuPreviewChecks.Cape(),Width=300,Height=400,Animate=false};
            var window=new Window{Title="方隅 GPU 隔离检查",Width=380,Height=460,ShowActivated=false,Position=new PixelPoint(-10000,-10000),Content=new Grid{Background=Brushes.White,Children={preview}}};desktop.MainWindow=window;
            window.Opened+=async(_,_)=>
            {
                try{await GpuPreviewChecks.RunAsync(preview,window,Root);desktop.Shutdown(0);}
                catch(Exception ex){Console.Error.WriteLine(ex);desktop.Shutdown(1);}
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
internal static class GpuPreviewChecks
{
    public static int Start(string root)
    {
        GpuTestApplication.Root=root;
        return AppBuilder.Configure<GpuTestApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
    }
    public static async Task RunAsync(PlayerPreview preview,Window window,string root)
    {
        void Check(string title,bool value){if(!value)throw new InvalidOperationException(title+" · "+preview.RendererDescription);Console.WriteLine("PASS "+title);}
        async Task Until(Func<bool> predicate){var end=DateTime.UtcNow.AddSeconds(15);while(!predicate()&&DateTime.UtcNow<end)await Task.Delay(30);Check("GPU 状态在时限内就绪",predicate());}
        await Until(()=>preview.IsGpuReady);
        Console.WriteLine("Device "+preview.RendererDescription);
        async Task<byte[]> Capture(string name)
        {
            var completion=new TaskCompletionSource<(byte[],PixelSize)>(TaskCreationOptions.RunContinuationsAsynchronously);
            preview.Gpu!.Readback=(bytes,size)=>completion.TrySetResult((bytes,size));preview.Gpu.RequestNextFrameRendering();
            var (pixels,size)=await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var image=new WriteableBitmap(size,new Avalonia.Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Premul);
            using(var frame=image.Lock())for(var y=0;y<size.Height;y++)Marshal.Copy(pixels,(size.Height-1-y)*size.Width*4,frame.Address+y*frame.RowBytes,size.Width*4);
            image.Save(Path.Combine(root,name+".png"));return pixels;
        }
        var front=await Capture("gpu-front");Check("GPU 渲染不是透明空帧",front.Where((_,i)=>i%4==3).Count(a=>a>0)>5000);
        preview.ShowBack();var back=await Capture("gpu-cape-back");
        int Band(byte[] data,int band){var n=0;for(var i=0;i<data.Length;i+=4){var r=data[i];var g=data[i+1];var b=data[i+2];if(data[i+3]>200&&(band switch{0=>r>130&&g<80&&b<80,1=>r>130&&g>100&&b<80,2=>g>100&&r<80&&b<100,_=>b>130&&r<80&&g<100}))n++;}return n;}
        Check("披风从顶部到底部四段图案均完整显示",Enumerable.Range(0,4).All(i=>Band(back,i)>250));
        var width=(int)(preview.Bounds.Width*(TopLevel.GetTopLevel(preview)?.RenderScaling??1));
        var markerX=new List<int>();var redX=new List<int>();for(var i=0;i<back.Length;i+=4){if(back[i+3]<200)continue;var x=i/4%width;if(back[i]>150&&Math.Abs(back[i]-back[i+1])<8&&Math.Abs(back[i]-back[i+2])<8)markerX.Add(x);if(back[i]>130&&back[i+1]<80&&back[i+2]<80)redX.Add(x);}
        Check("披风外侧图案左右方向保持原图",markerX.Count>100&&redX.Count>100&&markerX.Average()<redX.Average());
        preview.Gpu!.Seconds=1;preview.Gpu.Animate=true;var pose1=await Capture("gpu-cape-motion-1");preview.Gpu.Seconds=2.1;var pose2=await Capture("gpu-cape-motion-2");
        Check("GPU 骨骼姿态和披风动态随时间改变",!pose1.SequenceEqual(pose2));
        var bones=PlayerScene.Pose(2,true,true);Check("分段披风关节连续无断裂",Enumerable.Range(1,PlayerScene.CapeSegments-1).All(i=>Vector3.Distance(Vector3.Transform(new(0,-2,0),bones[5+i]),Vector3.Transform(Vector3.Zero,bones[6+i]))<0.0001f));
        preview.Zoom=1.35;var zoomed=await Capture("gpu-zoom");Check("镜头缩放实际改变 GPU 输出",!zoomed.SequenceEqual(back));
        preview.Zoom=9;Check("镜头缩放限制有效",preview.Zoom==1.65);preview.Zoom=0.01;Check("镜头缩小限制有效",preview.Zoom==0.65);
        preview.ResetView();Check("一键复位恢复镜头参数",preview.Zoom==1&&preview.Rotation==-22&&preview.Pitch==-8);
        preview.Rotation=155;preview.Gpu!.Animate=true;
        for(var frame=0;frame<24;frame++){preview.Gpu.Seconds=frame*0.12;await Capture("motion-"+frame.ToString("D2"));}
        preview.IsVisible=false;await Task.Delay(100);var frameCount=preview.Gpu!.FramesRendered;await Task.Delay(180);Check("隐藏预览后停止动画与绘制",!preview.IsAnimationRunning&&preview.Gpu.FramesRendered==frameCount);
        preview.IsVisible=true;preview.Slim=true;preview.Cape=null;await Capture("gpu-slim-no-cape");Check("移除披风与细手臂切换保持 GPU 可用",preview.IsGpuReady);
        preview.Skin=null;Check("关闭预览释放 GPU 控件",preview.Gpu is null&&!preview.IsAnimationRunning);
        Console.WriteLine("GPU checks passed");
    }
    public static byte[] Cape()
    {
        using var bitmap=new WriteableBitmap(new PixelSize(64,32),new Avalonia.Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Unpremul);
        using(var frame=bitmap.Lock())
        {
            var bytes=new byte[frame.RowBytes*32];var colors=new[]{new byte[]{240,25,35},new byte[]{240,205,20},new byte[]{25,220,45},new byte[]{30,55,240}};
            for(var y=1;y<=16;y++)for(var x=0;x<22;x++){var i=y*frame.RowBytes+x*4;var c=colors[(y-1)/4];bytes[i]=c[0];bytes[i+1]=c[1];bytes[i+2]=c[2];bytes[i+3]=255;}
            for(var y=1;y<=16;y++)for(var x=1;x<=2;x++){var i=y*frame.RowBytes+x*4;bytes[i]=bytes[i+1]=bytes[i+2]=250;}
            Marshal.Copy(bytes,0,frame.Address,bytes.Length);
        }
        using var stream=new MemoryStream();bitmap.Save(stream);return stream.ToArray();
    }
}
