using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace Launcher.Desktop.Controls;

/// <summary>Orthographic textured cuboids, repainted only when the texture or viewing angle changes.</summary>
public sealed class PlayerPreview:Control
{
    public static readonly StyledProperty<byte[]?> SkinProperty=AvaloniaProperty.Register<PlayerPreview,byte[]?>(nameof(Skin));
    public static readonly StyledProperty<byte[]?> CapeProperty=AvaloniaProperty.Register<PlayerPreview,byte[]?>(nameof(Cape));
    public static readonly StyledProperty<bool> SlimProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(Slim));
    public static readonly StyledProperty<bool> OuterLayerProperty=AvaloniaProperty.Register<PlayerPreview,bool>(nameof(OuterLayer),true);
    public static readonly StyledProperty<double> RotationProperty=AvaloniaProperty.Register<PlayerPreview,double>(nameof(Rotation),-22);
    public byte[]? Skin{get=>GetValue(SkinProperty);set=>SetValue(SkinProperty,value);}
    public byte[]? Cape{get=>GetValue(CapeProperty);set=>SetValue(CapeProperty,value);}
    public bool Slim{get=>GetValue(SlimProperty);set=>SetValue(SlimProperty,value);}
    public bool OuterLayer{get=>GetValue(OuterLayerProperty);set=>SetValue(OuterLayerProperty,value);}
    public double Rotation{get=>GetValue(RotationProperty);set=>SetValue(RotationProperty,value);}
    private TextureSet? _skin,_cape;
    private Point? _drag;
    private sealed record Face(Point[] Points,double Depth,Rect Texture,Bitmap? Image,double Shade);
    private readonly record struct V(double X,double Y,double Z);
    static PlayerPreview()=>AffectsRender<PlayerPreview>(SkinProperty,CapeProperty,SlimProperty,OuterLayerProperty,RotationProperty);
    public PlayerPreview(){Focusable=true;MinHeight=200;RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.None);ToolTip.SetTip(this,"拖动旋转角色；方向键也可旋转。纹理预览不代表游戏内已应用。");}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==SkinProperty){_skin?.Dispose();_skin=Decode(Skin);}
        if(change.Property==CapeProperty){_cape?.Dispose();_cape=Decode(Cape);}
    }
    private static TextureSet? Decode(byte[]? data)
    {
        if(data is null)return null;
        try {using var stream=new MemoryStream(data);using var original=new Bitmap(stream);return new TextureSet(original);}catch(ArgumentException){return null;}
    }
    // Cache face lighting in the texture, preserving alpha even on translucent outer layers.
    // Normalizing DPI keeps atlas coordinates in pixels, independent of PNG metadata.
    private sealed class TextureSet:IDisposable
    {
        public Bitmap Image {get;}
        private readonly Dictionary<double,Bitmap> _shaded=[];
        public TextureSet(Bitmap original)=>Image=Copy(original,0);
        public Bitmap At(double shade)
        {
            if(shade==0)return Image;
            if(!_shaded.TryGetValue(shade,out var bitmap))_shaded[shade]=bitmap=Copy(Image,shade);
            return bitmap;
        }
        private static Bitmap Copy(Bitmap source,double shade)
        {
            var bitmap=new WriteableBitmap(source.PixelSize,new Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Unpremul);
            using var frame=bitmap.Lock();source.CopyPixels(frame);
            if(shade>0)
            {
                var bytes=new byte[frame.RowBytes*frame.Size.Height];Marshal.Copy(frame.Address,bytes,0,bytes.Length);
                for(var y=0;y<frame.Size.Height;y++)for(var x=0;x<frame.Size.Width;x++)
                {var i=y*frame.RowBytes+x*4;for(var c=0;c<3;c++)bytes[i+c]=(byte)Math.Round(bytes[i+c]*(1-shade));}
                Marshal.Copy(bytes,0,frame.Address,bytes.Length);
            }
            return bitmap;
        }
        public void Dispose(){Image.Dispose();foreach(var bitmap in _shaded.Values)bitmap.Dispose();}
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {base.OnPointerPressed(e);if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed){_drag=e.GetPosition(this);e.Pointer.Capture(this);Focus();e.Handled=true;}}
    protected override void OnPointerMoved(PointerEventArgs e)
    {base.OnPointerMoved(e);if(_drag is {} previous){var point=e.GetPosition(this);Rotation+=(point.X-previous.X)*0.7;_drag=point;e.Handled=true;}}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);_drag=null;e.Pointer.Capture(null);}
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){base.OnPointerCaptureLost(e);_drag=null;}
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.Key is Key.Left or Key.Right){Rotation+=e.Key==Key.Left?-15:15;e.Handled=true;}}
    public override void Render(DrawingContext context)
    {
        base.Render(context);if(Bounds.Width<=0||Bounds.Height<=0)return;
        var scale=Math.Min(Bounds.Width/26,Bounds.Height/36);var yaw=Rotation*Math.PI/180;const double pitch=-0.13;
        (Point,double) Project(V v)
        {var x=v.X*Math.Cos(yaw)+v.Z*Math.Sin(yaw);var z=-v.X*Math.Sin(yaw)+v.Z*Math.Cos(yaw);return(new(Bounds.Width/2+x*scale,Bounds.Height/2-((v.Y-16)*Math.Cos(pitch)-z*Math.Sin(pitch))*scale),z);}
        var faces=new List<Face>();
        void Box(double x,double y,double z,double w,double h,double d,int tx,int ty,TextureSet? texture,double grow=0)
        {
            var l=x-w/2-grow;var r=x+w/2+grow;var b=y-grow;var t=y+h+grow;var front=z-d/2-grow;var back=z+d/2+grow;
            void Face(V a,V b,V c,V e,Rect uv,double shade)
            {
                if(texture is not null&&(uv.Right>texture.Image.PixelSize.Width||uv.Bottom>texture.Image.PixelSize.Height))return;
                var points=new[]{Project(a),Project(b),Project(c),Project(e)};
                var edge=points[1].Item1-points[0].Item1;var side=points[3].Item1-points[0].Item1;
                if(edge.X*side.Y-edge.Y*side.X<=0.0001)return; // Hide inward faces, including behind transparent hat pixels.
                faces.Add(new(points.Select(p=>p.Item1).ToArray(),points.Average(p=>p.Item2),uv,texture?.At(shade),shade));
            }
            Face(new(l,t,front),new(r,t,front),new(r,b,front),new(l,b,front),new(tx+d,ty+d,w,h),0);
            Face(new(r,t,back),new(l,t,back),new(l,b,back),new(r,b,back),new(tx+2*d+w,ty+d,w,h),0.12);
            Face(new(l,t,back),new(l,t,front),new(l,b,front),new(l,b,back),new(tx,ty+d,d,h),0.17);
            Face(new(r,t,front),new(r,t,back),new(r,b,back),new(r,b,front),new(tx+d+w,ty+d,d,h),0.1);
            Face(new(l,t,back),new(r,t,back),new(r,t,front),new(l,t,front),new(tx+d,ty,w,d),0);
            Face(new(l,b,front),new(r,b,front),new(r,b,back),new(l,b,back),new(tx+d+w,ty,w,d),0.22);
        }
        var arm=Slim?3:4;var armX=4+arm/2d;var modern=_skin?.Image.PixelSize.Height!=32;
        Box(0,24,0,8,8,8,0,0,_skin);Box(0,12,0,8,12,4,16,16,_skin);
        Box(-armX,12,0,arm,12,4,40,16,_skin);Box(armX,12,0,arm,12,4,modern?32:40,modern?48:16,_skin);
        Box(-2,0,0,4,12,4,0,16,_skin);Box(2,0,0,4,12,4,modern?16:0,modern?48:16,_skin);
        if(OuterLayer&&_skin is not null)
        {
            Box(0,24,0,8,8,8,32,0,_skin,0.4);
            if(modern){Box(0,12,0,8,12,4,16,32,_skin,0.22);Box(-armX,12,0,arm,12,4,40,32,_skin,0.22);Box(armX,12,0,arm,12,4,48,48,_skin,0.22);Box(-2,0,0,4,12,4,0,32,_skin,0.22);Box(2,0,0,4,12,4,0,48,_skin,0.22);}
        }
        if(_cape is not null)Box(0,7.5,3,10,16,1,0,0,_cape);
        foreach(var face in faces.OrderByDescending(f=>f.Depth))
        {
            var a=face.Points[0];var b=face.Points[1];var d=face.Points[3];var uv=face.Texture;
            var matrix=new Matrix((b.X-a.X)/uv.Width,(b.Y-a.Y)/uv.Width,(d.X-a.X)/uv.Height,(d.Y-a.Y)/uv.Height,a.X,a.Y);
            if(Math.Abs(matrix.GetDeterminant())<0.0001)continue;
            using(context.PushTransform(matrix))
            {
                var destination=new Rect(0,0,uv.Width,uv.Height);
                if(face.Image is {} image)context.DrawImage(image,uv,destination);
                else context.DrawRectangle(new SolidColorBrush(Color.FromRgb(150,165,153)),null,destination);
                // Shade opaque base geometry only; transparent outer layers retain their alpha.
                if(face.Shade>0&&face.Image is null)context.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(face.Shade*255),0,0,0)),null,destination);
            }
        }
    }
}
