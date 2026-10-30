using System.Numerics;

namespace Launcher.Desktop.Controls;

// World units match skin pixels. The front points toward +Z; the cape hangs behind -Z.
internal sealed class PlayerScene
{
    public const int CapeSegments=8;
    public const int BoneCount=6+CapeSegments;
    public const int Stride=9;
    public float[] Vertices {get;}
    public int BaseCount {get;}
    public int OuterCount {get;}
    public int CapeCount {get;}
    public PlayerScene(bool slim,bool legacy)
    {
        var data=new List<float>();var arm=slim?3:4;var skinHeight=legacy?32:64;
        void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,float u,float v,float w,float h,int bone,bool cape=false,bool mirror=false)
        {
            var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));
            void Vertex(Vector3 p,float x,float y){data.AddRange([p.X,p.Y,p.Z,x/64,y/(cape?32:skinHeight),normal.X,normal.Y,normal.Z,bone]);}
            var u0=mirror?u+w:u;var u1=mirror?u:u+w;
            Vertex(a,u0,v);Vertex(b,u1,v);Vertex(c,u1,v+h);Vertex(a,u0,v);Vertex(c,u1,v+h);Vertex(d,u0,v+h);
        }
        void Box(float w,float h,float depth,int tx,int ty,int bone,float yCenter,float grow=0,bool mirror=false)
        {
            var l=-w/2-grow;var r=w/2+grow;var t=yCenter+h/2+grow;var b=yCenter-h/2-grow;var f=depth/2+grow;var z=-f;
            // Outward counter-clockwise winding; UVs are top-down atlas coordinates.
            Quad(new(r,t,f),new(l,t,f),new(l,b,f),new(r,b,f),tx+depth,ty+depth,w,h,bone,mirror:!mirror);
            Quad(new(l,t,z),new(r,t,z),new(r,b,z),new(l,b,z),tx+2*depth+w,ty+depth,w,h,bone,mirror:!mirror);
            Quad(new(l,t,f),new(l,t,z),new(l,b,z),new(l,b,f),tx,ty+depth,depth,h,bone,mirror:!mirror);
            Quad(new(r,t,z),new(r,t,f),new(r,b,f),new(r,b,z),tx+depth+w,ty+depth,depth,h,bone,mirror:!mirror);
            Quad(new(l,t,z),new(l,t,f),new(r,t,f),new(r,t,z),tx+depth,ty,w,depth,bone,mirror:mirror);
            Quad(new(l,b,f),new(l,b,z),new(r,b,z),new(r,b,f),tx+depth+w,ty,w,depth,bone,mirror:mirror);
        }
        void Body(bool outer)
        {
            var g=outer?0.25f:0;
            Box(8,8,8,outer?32:0,0,1,4,outer?0.5f:0);
            if(outer&&legacy)return;
            Box(8,12,4,16,outer?32:16,0,-6,g);
            Box(arm,12,4,40,outer?32:16,2,-6,g);
            Box(arm,12,4,outer?48:legacy?40:32,legacy?16:48,3,-6,g,legacy);
            Box(4,12,4,0,outer?32:16,4,-6,g);
            Box(4,12,4,outer?0:legacy?0:16,legacy?16:48,5,-6,g,legacy);
        }
        Body(false);BaseCount=data.Count/Stride;Body(true);OuterCount=data.Count/Stride-BaseCount;
        for(var i=0;i<CapeSegments;i++)
        {
            var y=i*2f;var bone=6+i;
            // One continuous 10x16 image across all strips, not a full image repeated per strip.
            // The outside of the cape uses the large (1,1)-(11,17) rectangle.
            Quad(new(-5,0,-0.5f),new(5,0,-0.5f),new(5,-2,-0.5f),new(-5,-2,-0.5f),1,1+y,10,2,bone,true,true);
            Quad(new(5,0,0.5f),new(-5,0,0.5f),new(-5,-2,0.5f),new(5,-2,0.5f),12,1+y,10,2,bone,true,true);
            Quad(new(-5,0,0.5f),new(-5,0,-0.5f),new(-5,-2,-0.5f),new(-5,-2,0.5f),0,1+y,1,2,bone,true);
            Quad(new(5,0,-0.5f),new(5,0,0.5f),new(5,-2,0.5f),new(5,-2,-0.5f),11,1+y,1,2,bone,true);
            if(i==0)Quad(new(-5,0,-0.5f),new(-5,0,0.5f),new(5,0,0.5f),new(5,0,-0.5f),1,0,10,1,bone,true);
            if(i==CapeSegments-1)Quad(new(-5,-2,0.5f),new(-5,-2,-0.5f),new(5,-2,-0.5f),new(5,-2,0.5f),11,0,10,1,bone,true);
        }
        CapeCount=data.Count/Stride-BaseCount-OuterCount;Vertices=data.ToArray();
    }
    public static Matrix4x4[] Pose(double seconds,bool slim,bool animate)
    {
        var t=animate?(float)seconds:0;var swing=animate?MathF.Sin(t*1.5f):0;var breath=animate?MathF.Sin(t*2)*0.08f:0;var armX=4+(slim?1.5f:2);
        var bones=new Matrix4x4[BoneCount];
        bones[0]=Matrix4x4.CreateTranslation(0,24+breath,0);
        bones[1]=Matrix4x4.CreateRotationY(swing*0.045f)*Matrix4x4.CreateTranslation(0,24+breath,0);
        bones[2]=Matrix4x4.CreateRotationX(swing*0.07f)*Matrix4x4.CreateRotationZ(-0.045f)*Matrix4x4.CreateTranslation(-armX,24+breath,0);
        bones[3]=Matrix4x4.CreateRotationX(-swing*0.07f)*Matrix4x4.CreateRotationZ(0.045f)*Matrix4x4.CreateTranslation(armX,24+breath,0);
        bones[4]=Matrix4x4.CreateRotationX(-swing*0.025f)*Matrix4x4.CreateTranslation(-2,12+breath,0);
        bones[5]=Matrix4x4.CreateRotationX(swing*0.025f)*Matrix4x4.CreateTranslation(2,12+breath,0);
        var cape=Matrix4x4.CreateRotationX(0.16f+(animate?MathF.Sin(t*1.3f)*0.035f:0))*Matrix4x4.CreateTranslation(0,23.5f+breath,-3.25f);
        for(var i=0;i<CapeSegments;i++)
        {
            if(i>0)cape=Matrix4x4.CreateRotationX(0.015f+(animate?MathF.Sin(t*1.8f-i*0.45f)*0.018f:0))*Matrix4x4.CreateTranslation(0,-2,0)*cape;
            bones[6+i]=cape;
        }
        return bones;
    }
    public static Matrix4x4 Camera(float aspect,double yaw,double pitch,double zoom)
    {
        var radians=(float)(Math.PI/180);var view=Matrix4x4.CreateLookAt(new(0,17,64/(float)zoom),new(0,16,0),Vector3.UnitY);
        // OpenGL clip depth is [-1,1], unlike Matrix4x4.CreatePerspectiveFieldOfView's [0,1].
        var f=1/MathF.Tan(34*radians/2);const float near=1,far=200;
        var projection=new Matrix4x4(f/aspect,0,0,0,0,f,0,0,0,0,(far+near)/(near-far),-1,0,0,2*far*near/(near-far),0);
        return Matrix4x4.CreateTranslation(0,-16,0)*Matrix4x4.CreateRotationY((float)yaw*radians)*Matrix4x4.CreateRotationX((float)pitch*radians)*Matrix4x4.CreateTranslation(0,16,0)*view*projection;
    }
}
