using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using static Avalonia.OpenGL.GlConsts;

namespace Launcher.Desktop.Controls;

internal sealed unsafe class PlayerGlPreview:OpenGlControlBase
{
    private int _program,_buffer,_vao,_depth,_skinTexture,_capeTexture,_mvp,_bones,_sampler,_opaque;
    private PixelSize _size;
    private byte[]? _skinBytes,_capeBytes;
    private bool _geometryDirty=true,_textureDirty=true,_failed;
    private PlayerScene? _scene;
    public byte[]? Skin {get=>_skinBytes;set{if(ReferenceEquals(value,_skinBytes))return;_skinBytes=value;_textureDirty=_geometryDirty=true;}}
    public byte[]? Cape {get=>_capeBytes;set{if(ReferenceEquals(value,_capeBytes))return;_capeBytes=value;_textureDirty=true;}}
    public bool Slim {get;set;}
    public bool OuterLayer {get;set;}=true;
    public double Yaw {get;set;}=-22;
    public double Pitch {get;set;}=-8;
    public double Zoom {get;set;}=1;
    public double Seconds {get;set;}
    public bool Animate {get;set;}
    private bool _lastSlim;
    public int FramesRendered {get;private set;}
    public string Device {get;private set;}="";
    public event Action<bool,string>? RenderState;
    internal Action<byte[],PixelSize>? Readback {get;set;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BlendSeparate(int a,int b,int c,int d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ReadPixels(int x,int y,int w,int h,int format,int type,IntPtr target);
    private BlendSeparate? _blend;
    private ReadPixels? _read;
    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            Device=$"{gl.Renderer} · {gl.Version}";
            _blend=Marshal.GetDelegateForFunctionPointer<BlendSeparate>(gl.GetProcAddress("glBlendFuncSeparate"));
            _read=Marshal.GetDelegateForFunctionPointer<ReadPixels>(gl.GetProcAddress("glReadPixels"));
            var es=GlVersion.Type==GlProfileType.OpenGLES;
            var modern=GlVersion.Major>=3;
            var prefix=modern?(es?"#version 300 es\nprecision highp float;\n":"#version 150\n"):es?"#version 100\nprecision highp float;\n":"#version 120\n";
            var input=modern?"in":"attribute";var output=modern?"out":"varying";
            var vertex=prefix+$$"""
                {{input}} vec3 aPosition;
                {{input}} vec2 aUv;
                {{input}} vec3 aNormal;
                {{input}} float aBone;
                uniform mat4 uMvp;
                uniform mat4 uBones[14];
                {{output}} vec2 vUv;
                {{output}} float vLight;
                void main() {
                    mat4 bone=uBones[int(aBone+0.5)];
                    vec3 normal=normalize(mat3(bone)*aNormal);
                    vLight=0.72+0.28*max(dot(normal,normalize(vec3(-0.4,0.8,0.65))),0.0);
                    vUv=aUv;
                    gl_Position=uMvp*bone*vec4(aPosition,1.0);
                }
                """;
            var fragment=prefix+$$"""
                {{(modern?"in":"varying")}} vec2 vUv;
                {{(modern?"in":"varying")}} float vLight;
                uniform sampler2D uTexture;
                uniform float uOpaque;
                {{(modern?"out vec4 color;":"")}}
                void main() {
                    vec4 texel={{(modern?"texture":"texture2D")}}(uTexture,vUv);
                    texel.a=mix(texel.a,1.0,uOpaque);
                    if(texel.a<0.01)discard;
                    {{(modern?"color":"gl_FragColor")}}=vec4(texel.rgb*vLight,texel.a);
                }
                """;
            var vs=Compile(gl,GL_VERTEX_SHADER,vertex);var fs=Compile(gl,GL_FRAGMENT_SHADER,fragment);
            try
            {
                _program=gl.CreateProgram();gl.AttachShader(_program,vs);gl.AttachShader(_program,fs);
                gl.BindAttribLocationString(_program,0,"aPosition");gl.BindAttribLocationString(_program,1,"aUv");gl.BindAttribLocationString(_program,2,"aNormal");gl.BindAttribLocationString(_program,3,"aBone");
                if(gl.LinkProgramAndGetError(_program) is {} error)throw new InvalidOperationException("角色着色程序链接失败："+error);
            }
            finally{gl.DeleteShader(vs);gl.DeleteShader(fs);}
            _mvp=gl.GetUniformLocationString(_program,"uMvp");_bones=gl.GetUniformLocationString(_program,"uBones[0]");_sampler=gl.GetUniformLocationString(_program,"uTexture");_opaque=gl.GetUniformLocationString(_program,"uOpaque");
            _buffer=gl.GenBuffer();_depth=gl.GenRenderbuffer();_skinTexture=gl.GenTexture();_capeTexture=gl.GenTexture();
            if(gl.IsGenVertexArraysAvailable)_vao=gl.GenVertexArray();
            _textureDirty=_geometryDirty=true;_failed=false;
        }
        catch(Exception ex){Fail(ex);}
    }
    private static int Compile(GlInterface gl,int type,string source)
    {var shader=gl.CreateShader(type);if(gl.CompileShaderAndGetError(shader,source) is {} error){gl.DeleteShader(shader);throw new InvalidOperationException("角色着色程序编译失败："+error);}return shader;}
    protected override void OnOpenGlRender(GlInterface gl,int fb)
    {
        if(_failed||_program==0)return;
        try
        {
            var scaling=TopLevel.GetTopLevel(this)?.RenderScaling??1;
            var size=new PixelSize(Math.Max(1,(int)(Bounds.Width*scaling)),Math.Max(1,(int)(Bounds.Height*scaling)));
            gl.BindFramebuffer(GL_FRAMEBUFFER,fb);
            gl.BindRenderbuffer(GL_RENDERBUFFER,_depth);
            if(size!=_size){gl.RenderbufferStorage(GL_RENDERBUFFER,0x81A5,size.Width,size.Height);_size=size;}
            gl.FramebufferRenderbuffer(GL_FRAMEBUFFER,GL_DEPTH_ATTACHMENT,GL_RENDERBUFFER,_depth);
            if(gl.CheckFramebufferStatus(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE)throw new InvalidOperationException("角色深度缓冲不可用。");
            gl.Viewport(0,0,size.Width,size.Height);gl.ClearColor(0,0,0,0);gl.ClearDepth(1);gl.DepthMask(1);gl.Clear(GL_COLOR_BUFFER_BIT|GL_DEPTH_BUFFER_BIT);
            if(_textureDirty){Upload(gl,_skinTexture,Skin,false);Upload(gl,_capeTexture,Cape,true);_textureDirty=false;}
            if(_geometryDirty||_lastSlim!=Slim)
            {
                _scene=new(Slim,Skin is {Length:>24}&&System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Skin.AsSpan(20,4))==32);_lastSlim=Slim;
                gl.BindBuffer(GL_ARRAY_BUFFER,_buffer);fixed(float* vertices=_scene.Vertices)gl.BufferData(GL_ARRAY_BUFFER,(IntPtr)(_scene.Vertices.Length*sizeof(float)),(IntPtr)vertices,GL_STATIC_DRAW);_geometryDirty=false;
            }
            if(_scene is null)return;
            gl.UseProgram(_program);if(_vao!=0)gl.BindVertexArray(_vao);gl.BindBuffer(GL_ARRAY_BUFFER,_buffer);
            foreach(var attr in new[]{(0,3,0),(1,2,3),(2,3,5),(3,1,8)}){gl.EnableVertexAttribArray(attr.Item1);gl.VertexAttribPointer(attr.Item1,attr.Item2,GL_FLOAT,0,PlayerScene.Stride*sizeof(float),(IntPtr)(attr.Item3*sizeof(float)));}
            var mvp=PlayerScene.Camera((float)size.Width/size.Height,Yaw,Pitch,Zoom);gl.UniformMatrix4fv(_mvp,1,false,&mvp);
            var bones=PlayerScene.Pose(Seconds,Slim,Animate);fixed(Matrix4x4* matrices=bones)gl.UniformMatrix4fv(_bones,bones.Length,false,matrices);
            gl.ActiveTexture(GL_TEXTURE0);gl.Uniform1i(_sampler,0);gl.Enable(GL_DEPTH_TEST);gl.DepthFunc(0x0203);gl.Enable(GL_CULL_FACE);gl.Enable(0x0BE2);_blend!(0x0302,0x0303,1,0x0303);
            gl.BindTexture(GL_TEXTURE_2D,_skinTexture);gl.Uniform1f(_opaque,1);gl.DrawArrays(GL_TRIANGLES,0,(IntPtr)_scene.BaseCount);
            if(Cape is not null){gl.BindTexture(GL_TEXTURE_2D,_capeTexture);gl.Uniform1f(_opaque,0);gl.DrawArrays(GL_TRIANGLES,_scene.BaseCount+_scene.OuterCount,(IntPtr)_scene.CapeCount);}
            if(OuterLayer&&Skin is not null){gl.DepthMask(0);gl.BindTexture(GL_TEXTURE_2D,_skinTexture);gl.Uniform1f(_opaque,0);gl.DrawArrays(GL_TRIANGLES,_scene.BaseCount,(IntPtr)_scene.OuterCount);gl.DepthMask(1);}
            gl.Disable(GL_CULL_FACE);gl.Disable(GL_DEPTH_TEST);gl.Disable(0x0BE2);if(_vao!=0)gl.BindVertexArray(0);gl.BindBuffer(GL_ARRAY_BUFFER,0);gl.UseProgram(0);
            FramesRendered++;
            if(FramesRendered==1)RenderState?.Invoke(true,Device);
            if(Readback is {} capture){Readback=null;var pixels=new byte[size.Width*size.Height*4];fixed(byte* ptr=pixels)_read!(0,0,size.Width,size.Height,GL_RGBA,GL_UNSIGNED_BYTE,(IntPtr)ptr);capture(pixels,size);}
        }
        catch(Exception ex){Fail(ex);}
    }
    private static void Upload(GlInterface gl,int texture,byte[]? png,bool cape)
    {
        using var source=png is null?null:new Bitmap(new MemoryStream(png));
        using var rgba=new WriteableBitmap(source?.PixelSize??new PixelSize(64,cape?32:64),new Avalonia.Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Unpremul);
        using var frame=rgba.Lock();
        if(source is not null)source.CopyPixels(frame);else{var bytes=Enumerable.Repeat((byte)160,frame.RowBytes*frame.Size.Height).ToArray();for(var i=3;i<bytes.Length;i+=4)bytes[i]=255;Marshal.Copy(bytes,0,frame.Address,bytes.Length);}
        gl.BindTexture(GL_TEXTURE_2D,texture);gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_NEAREST);gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_NEAREST);gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);gl.TexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);
        gl.TexImage2D(GL_TEXTURE_2D,0,GL_RGBA,frame.Size.Width,frame.Size.Height,0,GL_RGBA,GL_UNSIGNED_BYTE,frame.Address);
    }
    private void Fail(Exception ex){_failed=true;Serilog.Log.Warning(ex,"角色 GPU 预览不可用，使用兼容预览");RenderState?.Invoke(false,"兼容预览");}
    protected override void OnOpenGlLost(){_program=_buffer=_vao=_depth=_skinTexture=_capeTexture=0;_size=default;RenderState?.Invoke(false,"兼容预览");}
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if(_program!=0)gl.DeleteProgram(_program);if(_buffer!=0)gl.DeleteBuffer(_buffer);if(_vao!=0)gl.DeleteVertexArray(_vao);if(_depth!=0)gl.DeleteRenderbuffer(_depth);if(_skinTexture!=0)gl.DeleteTexture(_skinTexture);if(_capeTexture!=0)gl.DeleteTexture(_capeTexture);
        _program=_buffer=_vao=_depth=_skinTexture=_capeTexture=0;_size=default;RenderState?.Invoke(false,"兼容预览");
    }
}
