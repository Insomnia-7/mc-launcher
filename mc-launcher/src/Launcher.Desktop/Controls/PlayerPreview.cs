using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Launcher.Desktop.Controls;

public sealed class PlayerPreview:Grid
{
    public static readonly StyledProperty<byte[]?> SkinProperty=AvaloniaProperty.Register<PlayerPreview,byte[]?>(nameof(Skin));
    public static readonly StyledProperty<byte[]?> CapeProperty=AvaloniaProperty.Register<PlayerPreview,byte[]?>(nameof(Cape));
    public static readonly StyledProperty<bool> SlimProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(Slim));
    public static readonly StyledProperty<bool> OuterLayerProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(OuterLayer),true);
    public static readonly StyledProperty<double> RotationProperty=AvaloniaProperty.Register<PlayerPreview,double>(nameof(Rotation),-22);
    public static readonly StyledProperty<double> PitchProperty=AvaloniaProperty.Register<PlayerPreview,double>(nameof(Pitch),-8,coerce:(_,v)=>Math.Clamp(v,-55,55));
    public static readonly StyledProperty<double> ZoomProperty=AvaloniaProperty.Register<PlayerPreview,double>(nameof(Zoom),1,coerce:(_,v)=>Math.Clamp(double.IsFinite(v)?v:1,0.65,1.65));
    public static readonly StyledProperty<bool> AnimateProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(Animate),true);
    public static readonly StyledProperty<bool> PreferGpuProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(PreferGpu),true);
    public static readonly StyledProperty<bool> PreviewActiveProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(PreviewActive),true);
    public bool PreviewActive{get=>GetValue(PreviewActiveProperty);set=>SetValue(PreviewActiveProperty,value);}
    public byte[]? Skin{get=>GetValue(SkinProperty);set=>SetValue(SkinProperty,value);}
    public byte[]? Cape{get=>GetValue(CapeProperty);set=>SetValue(CapeProperty,value);}
    public bool Slim{get=>GetValue(SlimProperty);set=>SetValue(SlimProperty,value);}
    public bool OuterLayer{get=>GetValue(OuterLayerProperty);set=>SetValue(OuterLayerProperty,value);}
    public double Rotation{get=>GetValue(RotationProperty);set=>SetValue(RotationProperty,value);}
    public double Pitch{get=>GetValue(PitchProperty);set=>SetValue(PitchProperty,value);}
    public double Zoom{get=>GetValue(ZoomProperty);set=>SetValue(ZoomProperty,value);}
    public bool Animate{get=>GetValue(AnimateProperty);set=>SetValue(AnimateProperty,value);}
    public bool PreferGpu{get=>GetValue(PreferGpuProperty);set=>SetValue(PreferGpuProperty,value);}
    private readonly SoftwarePlayerPreview _software=new(){IsHitTestVisible=false};
    private PlayerGlPreview? _gpu;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(33)};
    private readonly Stopwatch _clock=new();
    private Window? _window;
    private Point? _drag;
    private bool _ready;
    public bool IsGpuReady=>_ready;
    public string RendererDescription {get;private set;}="兼容预览";
    public bool IsAnimationRunning=>_timer.IsEnabled;
    internal PlayerGlPreview? Gpu=>_gpu;
    public PlayerPreview()
    {
        Focusable=true;MinHeight=200;ClipToBounds=true;Background=Brushes.Transparent;
        RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.None);
        Children.Add(_software);_timer.Tick+=(_,_)=>{if(!ShouldAnimate()){UpdateAnimation();return;}SyncFrame();};
        ToolTip.SetTip(this,"拖动旋转与俯仰 · 滚轮缩放 · 方向键旋转 · +/- 缩放 · Home 复位");
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);_window=TopLevel.GetTopLevel(this) as Window;
        if(_window is not null)_window.PropertyChanged+=WindowChanged;
        MotionPreferences.Changed+=UpdateAnimation;EnsureGpu();SyncFrame();UpdateAnimation();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();_clock.Stop();MotionPreferences.Changed-=UpdateAnimation;if(_window is not null)_window.PropertyChanged-=WindowChanged;_window=null;
        base.OnDetachedFromVisualTree(e);
    }
    private void WindowChanged(object? sender,AvaloniaPropertyChangedEventArgs e){if(e.Property.Name is "IsActive" or "WindowState" or "IsVisible")UpdateAnimation();}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);if(_software is null)return;
        if(change.Property==PreferGpuProperty||change.Property==SkinProperty||change.Property==PreviewActiveProperty)EnsureGpu();
        if(change.Property==SkinProperty||change.Property==CapeProperty||change.Property==SlimProperty||change.Property==OuterLayerProperty||change.Property==RotationProperty||change.Property==PitchProperty||change.Property==ZoomProperty||change.Property==AnimateProperty||change.Property==PreviewActiveProperty||change.Property==IsVisibleProperty)
        {SyncFrame();UpdateAnimation();}
        if(change.Property.Name=="RenderOptions")RenderOptions.SetBitmapInterpolationMode(_software,RenderOptions.GetBitmapInterpolationMode(this));
    }
    private void EnsureGpu()
    {
        if(!PreferGpu||Skin is null){if(_gpu is not null){Children.Remove(_gpu);_gpu=null;}_ready=false;_software.IsVisible=true;return;}
        if(_gpu is not null||!PreviewActive||!IsEffectivelyVisible||TopLevel.GetTopLevel(this) is null)return;
        _gpu=new(){IsHitTestVisible=false};
        _gpu.RenderState+=(ok,description)=>{_ready=ok;RendererDescription=description;_software.IsVisible=!ok;if(!ok&&_gpu is not null)_gpu.IsVisible=false;UpdateAnimation();};Children.Add(_gpu);
    }
    private bool ShouldAnimate()=>_ready&&PreviewActive&&Animate&&MotionPreferences.Enabled&&Skin is not null&&IsEffectivelyVisible&&_window is {IsActive:true,WindowState:not WindowState.Minimized};
    private void UpdateAnimation()
    {
        if(ShouldAnimate()){_clock.Start();_timer.Start();}else{_timer.Stop();_clock.Stop();}
        if(_gpu is not null){_gpu.Animate=Animate&&MotionPreferences.Enabled;_gpu.RequestNextFrameRendering();}
    }
    private void SyncFrame()
    {
        _software.Skin=Skin;_software.Cape=Cape;_software.Slim=Slim;_software.OuterLayer=OuterLayer;_software.Rotation=Rotation;_software.Zoom=Zoom;_software.Pitch=Pitch;
        if(_gpu is not null){_gpu.Skin=Skin;_gpu.Cape=Cape;_gpu.Slim=Slim;_gpu.OuterLayer=OuterLayer;_gpu.Yaw=Rotation;_gpu.Pitch=Pitch;_gpu.Zoom=Zoom;_gpu.Seconds=_clock.Elapsed.TotalSeconds;_gpu.Animate=Animate&&MotionPreferences.Enabled;_gpu.RequestNextFrameRendering();}
    }
    public void ResetView(){Rotation=-22;Pitch=-8;Zoom=1;}
    public void ShowBack(){Rotation=180;Pitch=-8;Zoom=1;}
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {base.OnPointerPressed(e);if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){_drag=e.GetPosition(this);e.Pointer.Capture(this);Focus();e.Handled=true;}}
    protected override void OnPointerMoved(PointerEventArgs e)
    {base.OnPointerMoved(e);if(_drag is {} previous){var p=e.GetPosition(this);Rotation+=(p.X-previous.X)*0.6;Pitch+=(p.Y-previous.Y)*0.35;_drag=p;e.Handled=true;}}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);_drag=null;e.Pointer.Capture(null);}
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){base.OnPointerCaptureLost(e);_drag=null;}
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e){base.OnPointerWheelChanged(e);Zoom*=Math.Exp(e.Delta.Y*0.1);e.Handled=true;}
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch(e.Key){case Key.Left:Rotation-=15;break;case Key.Right:Rotation+=15;break;case Key.Up:Pitch-=5;break;case Key.Down:Pitch+=5;break;case Key.Add:case Key.OemPlus:Zoom*=1.1;break;case Key.Subtract:case Key.OemMinus:Zoom/=1.1;break;case Key.Home:ResetView();break;default:return;}e.Handled=true;
    }
}
