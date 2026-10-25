namespace SwitchDesk.Desktop {
    internal static class LayoutGeometry {
        internal static System.Drawing.RectangleF StrokeBounds(int width,int height,float strokeWidth) {
            // GDI+ strokes are centered on the path, including at high DPI.
            float inset=strokeWidth/2F;
            return new System.Drawing.RectangleF(inset,inset,
                System.Math.Max(0,width-strokeWidth),System.Math.Max(0,height-strokeWidth));
        }
    }
}
