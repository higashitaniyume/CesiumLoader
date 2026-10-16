using System;
namespace ForkDirectionMod
{
    internal struct Point2
    {
        public float X, Y;
        public Point2(float x, float y) { X = x; Y = y; }
    }
    internal static class ArrowGeometry
    {
        public static bool TryLayout(Point2 approach, Point2 junction, Point2 exit, int rank, float width, out Point2 tail, out Point2 tip)
        {
            tail=new Point2(); tip=new Point2();
            float ox=exit.X-junction.X, oy=exit.Y-junction.Y;
            float outLength=(float)Math.Sqrt(ox*ox+oy*oy);
            float ix=approach.X-junction.X, iy=approach.Y-junction.Y;
            float inLength=(float)Math.Sqrt(ix*ix+iy*iy);
            if(outLength<2f || inLength<12f) return false;
            ox/=outLength; oy/=outLength;
            float separation=Math.Max(24f,width*4f);
            float side=rank==0 ? -1f : 1f;
            float anchorX=approach.X-iy/inLength*separation*side;
            float anchorY=approach.Y+ix/inLength*separation*side;
            float length=Math.Max(26f,Math.Min(42f,inLength*.42f));
            tail=new Point2(anchorX-ox*length*.5f,anchorY-oy*length*.5f);
            tip=new Point2(anchorX+ox*length*.5f,anchorY+oy*length*.5f);
            return true;
        }

        // FairyGUI PolygonMesh accepts positive signed area in its UI coordinates.
        // A seven-vertex arrow keeps a visible shaft and a broad triangular head.
        public static Point2[] Straight(Point2 start, Point2 end, float width)
        {
            float dx=end.X-start.X, dy=end.Y-start.Y;
            float length=(float)Math.Sqrt(dx*dx+dy*dy);
            if(length<8f || width<=0f) return new Point2[0];
            dx/=length; dy/=length;
            float shaft=Math.Min(width,length*.14f);
            float headLength=Math.Min(Math.Max(shaft*3.5f,10f),length*.48f);
            float headHalfWidth=Math.Max(shaft*1.65f,5f);
            float bx=end.X-dx*headLength, by=end.Y-dy*headLength;
            float nx=-dy, ny=dx;
            var points=new[] {
                new Point2(start.X+nx*shaft*.5f,start.Y+ny*shaft*.5f),
                new Point2(bx+nx*shaft*.5f,by+ny*shaft*.5f),
                new Point2(bx+nx*headHalfWidth,by+ny*headHalfWidth),
                end,
                new Point2(bx-nx*headHalfWidth,by-ny*headHalfWidth),
                new Point2(bx-nx*shaft*.5f,by-ny*shaft*.5f),
                new Point2(start.X-nx*shaft*.5f,start.Y-ny*shaft*.5f)
            };
            if(SignedArea(points)<0f) Array.Reverse(points);
            return points;
        }
        public static float SignedArea(Point2[] points)
        {
            float sum=0f;
            for(int i=0;i<points.Length;i++) {
                var a=points[i]; var b=points[(i+1)%points.Length];
                sum+=a.X*b.Y-b.X*a.Y;
            }
            return sum*.5f;
        }
    }
}
