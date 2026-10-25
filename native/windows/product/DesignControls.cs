using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SwitchDesk.Desktop {
    // Native, DPI-aware drawing primitives matching the approved desktop prototype.
    internal static class DeskTheme {
        internal static readonly Color Ink = ColorTranslator.FromHtml("#242933"), Muted = ColorTranslator.FromHtml("#7b818d"), Line = ColorTranslator.FromHtml("#e7e9ee"), Violet = ColorTranslator.FromHtml("#6554d9"), Soft = ColorTranslator.FromHtml("#f0edff"), Paper = Color.White, Background = ColorTranslator.FromHtml("#fbfcfd"), Sidebar = ColorTranslator.FromHtml("#f6f7f9"), Hero = ColorTranslator.FromHtml("#f1effb"), HeroLine = ColorTranslator.FromHtml("#e4dff5"), Green = ColorTranslator.FromHtml("#278270");
        internal static Font Font(float pixels, bool bold) { return new Font("Microsoft YaHei UI", pixels * .75F, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
        internal static GraphicsPath Round(RectangleF r, float radius) {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90); path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        internal static void Text(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags) { TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPadding); }
        internal static void Icon(Graphics g, string name, Color color, RectangleF r) {
            if (String.IsNullOrEmpty(name)) return;
            GraphicsState state = g.Save(); g.TranslateTransform(r.X, r.Y); g.ScaleTransform(r.Width / 24F, r.Height / 24F);
            using (var p = new Pen(color, 1.65F)) {
                p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                if (name == "monitor" || name == "console") { g.DrawRectangle(p, 3, 4, 18, 13); g.DrawLine(p, 12, 17, 12, 21); g.DrawLine(p, 8, 21, 16, 21); }
                else if (name == "laptop") { g.DrawRectangle(p, 5, 4, 14, 12); g.DrawLines(p, new[] { new Point(5,16),new Point(2,20),new Point(22,20),new Point(19,16) }); }
                else if (name == "swap") { g.DrawLine(p, 3, 8, 20, 8); g.DrawLines(p, new[] {new Point(16,4),new Point(20,8),new Point(16,12)}); g.DrawLine(p, 21, 17, 4, 17); g.DrawLines(p, new[] {new Point(8,13),new Point(4,17),new Point(8,21)}); }
                else if (name == "clipboard") { g.DrawRectangle(p, 5, 5, 14, 17); g.DrawRectangle(p, 9, 2, 6, 5); g.DrawLine(p, 9, 12, 15, 12); g.DrawLine(p, 9, 16, 15, 16); }
                else if (name == "keyboard") { g.DrawRectangle(p, 2, 5, 20, 14); for (int y=9;y<=13;y+=4) for(int x=6;x<=18;x+=4) g.DrawLine(p,x,y,x+.4F,y); g.DrawLine(p,8,16,16,16); }
                else if (name == "settings") { g.DrawEllipse(p, 8,8,8,8); for(int i=0;i<8;i++) { double a=i*Math.PI/4; g.DrawLine(p,12+(float)Math.Cos(a)*8,12+(float)Math.Sin(a)*8,12+(float)Math.Cos(a)*10,12+(float)Math.Sin(a)*10); } g.DrawEllipse(p,4,4,16,16); }
                else if (name == "shield") { g.DrawLines(p,new[]{new Point(12,2),new Point(21,6),new Point(19,15),new Point(12,22),new Point(5,15),new Point(3,6),new Point(12,2)}); g.DrawLine(p,12,7,12,13); g.DrawEllipse(p,11.6F,16,.8F,.8F); }
                else if (name == "folder") { g.DrawLines(p,new[]{new Point(3,6),new Point(10,6),new Point(12,9),new Point(21,9),new Point(21,20),new Point(3,20),new Point(3,6)}); }
                else if (name == "expand") { g.DrawLines(p,new[]{new Point(9,3),new Point(3,3),new Point(3,9)}); g.DrawLines(p,new[]{new Point(15,3),new Point(21,3),new Point(21,9)}); g.DrawLines(p,new[]{new Point(3,15),new Point(3,21),new Point(9,21)}); g.DrawLines(p,new[]{new Point(21,15),new Point(21,21),new Point(15,21)}); }
                else if (name == "pause") { g.DrawLine(p,8,5,8,19); g.DrawLine(p,16,5,16,19); }
                else if (name == "play") g.DrawPolygon(p,new[]{new Point(7,4),new Point(20,12),new Point(7,20)});
                else if (name == "chevron") g.DrawLines(p,new[]{new Point(8,5),new Point(15,12),new Point(8,19)});
            }
            g.Restore(state);
        }
    }

    internal class DeskPanel : Panel {
        private Color fill = DeskTheme.Paper, border = DeskTheme.Line;
        internal Color Fill { get { return fill; } set { fill=value; Invalidate(); } }
        internal Color Border { get { return border; } set { border=value; Invalidate(); } }
        internal float Radius = 11;
        internal DeskPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,true); BackColor=Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias; float scale=e.Graphics.DpiX/96F; using(var path=DeskTheme.Round(LayoutGeometry.StrokeBounds(Width,Height,scale),Radius*scale)) using(var brush=new SolidBrush(fill)) using(var pen=new Pen(border,scale)) { e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path); } }
    }

    internal enum DeskButtonStyle { Secondary, Primary, Navigation, Link, Source }
    internal sealed class DeskButton : Button {
        internal DeskButtonStyle Kind;
        internal string Glyph, Subtitle;
        private bool selected, hover;
        internal bool Selected { get { return selected; } set { selected=value; Invalidate(); } }
        internal DeskButton() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor,true);
            // ButtonBase defaults to Opaque. Rounded/link buttons need the parent
            // background painted before the reused double buffer is presented.
            SetStyle(ControlStyles.Opaque,false);
            BackColor=Color.Transparent; FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Font=DeskTheme.Font(12,false); Cursor=Cursors.Hand; UseVisualStyleBackColor=false;
        }
        protected override void OnMouseEnter(EventArgs e) { hover=true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover=false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; float s=g.DpiX/96F;
            Color fill=DeskTheme.Paper, border=DeskTheme.Line, text=DeskTheme.Ink;
            if(Kind==DeskButtonStyle.Primary) { fill=Enabled?(hover?ColorTranslator.FromHtml("#5847c8"):DeskTheme.Violet):ColorTranslator.FromHtml("#a9a0d2"); border=fill; text=Color.White; }
            else if(Kind==DeskButtonStyle.Navigation) { fill=selected?ColorTranslator.FromHtml("#eae7fa"):(hover?ColorTranslator.FromHtml("#eeeef3"):DeskTheme.Sidebar); border=fill; text=selected?DeskTheme.Violet:DeskTheme.Muted; }
            else if(Kind==DeskButtonStyle.Link) { fill=Parent is DeskPanel?((DeskPanel)Parent).Fill:Parent.BackColor; border=fill; text=DeskTheme.Muted; }
            else if(Kind==DeskButtonStyle.Source) { fill=selected?ColorTranslator.FromHtml("#f5f2ff"):ColorTranslator.FromHtml("#fcfcfd"); border=selected?ColorTranslator.FromHtml("#cec5f1"):DeskTheme.Line; }
            else if(hover&&Enabled) fill=DeskTheme.Sidebar;
            if(!Enabled&&Kind!=DeskButtonStyle.Primary) text=ColorTranslator.FromHtml("#747b88");
            using(var path=DeskTheme.Round(LayoutGeometry.StrokeBounds(Width,Height,s),8*s)) using(var b=new SolidBrush(fill)) using(var p=new Pen(border,s)) {g.FillPath(b,path);g.DrawPath(p,path);}
            int left=(int)(14*s), icon=(int)(18*s);
            if(Kind==DeskButtonStyle.Source) {
                RectangleF circle=new RectangleF(14*s,(Height-14*s)/2,14*s,14*s);
                using(var p=new Pen(selected?DeskTheme.Violet:ColorTranslator.FromHtml("#adb2bd"),s))g.DrawEllipse(p,circle);
                if(selected)using(var b=new SolidBrush(DeskTheme.Violet))g.FillEllipse(b,circle.X+4*s,circle.Y+4*s,6*s,6*s);
                left=(int)(39*s);
                using(var title=DeskTheme.Font(11,true))using(var sub=DeskTheme.Font(10,false)) {
                    DeskTheme.Text(g,Text,title,text,new Rectangle(left,(int)(9*s),Width-left-(int)(12*s),(int)(19*s)),TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
                    DeskTheme.Text(g,Subtitle,sub,DeskTheme.Muted,new Rectangle(left,(int)(29*s),Width-left-(int)(12*s),(int)(18*s)),TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
                }
            } else {
                if(!String.IsNullOrEmpty(Glyph)) { DeskTheme.Icon(g,Glyph,text,new RectangleF(left,(Height-icon)/2F,icon,icon)); left+=(int)(28*s); }
                DeskTheme.Text(g,Text,Font,text,new Rectangle(left,0,Math.Max(1,Width-left-(int)(10*s)),Height),TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|(Kind==DeskButtonStyle.Navigation?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter));
            }
            if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(g,new Rectangle((int)(4*s),(int)(4*s),Width-(int)(8*s),Height-(int)(8*s)),text,fill);
        }
    }

    internal sealed class DeskToggle : CheckBox {
        internal DeskToggle() {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);
            // CheckBox also inherits ButtonBase's Opaque style.
            SetStyle(ControlStyles.Opaque,false);
            AutoSize=false; BackColor=Color.Transparent; Cursor=Cursors.Hand; AccessibleRole=AccessibleRole.CheckButton;
        }
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; float s=g.DpiX/96F; float w=32*s,h=19*s,x=(Width-w)/2,y=(Height-h)/2;
            using(var p=DeskTheme.Round(new RectangleF(x,y,w,h),h/2))using(var b=new SolidBrush(Checked?DeskTheme.Violet:ColorTranslator.FromHtml("#d9dce3")))g.FillPath(b,p);
            using(var b=new SolidBrush(Color.White))g.FillEllipse(b,x+(Checked?15:2)*s,y+2*s,15*s,15*s);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,new Rectangle(1,1,Width-2,Height-2));
        }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    }

    internal sealed class DeskBadge : Control {
        internal bool Online;
        internal DeskBadge() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true); BackColor=Color.Transparent; Font=DeskTheme.Font(11,false); }
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=g.DpiX/96F;
            using(var p=DeskTheme.Round(LayoutGeometry.StrokeBounds(Width,Height,s),7*s))using(var b=new SolidBrush(Color.White))using(var line=new Pen(DeskTheme.Line,s)){g.FillPath(b,p);g.DrawPath(line,p);}
            using(var b=new SolidBrush(Online?DeskTheme.Green:DeskTheme.Muted))g.FillEllipse(b,10*s,(Height-6*s)/2,6*s,6*s);
            DeskTheme.Text(g,Text,Font,DeskTheme.Muted,new Rectangle((int)(24*s),0,Width-(int)(30*s),Height),TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class DeskDevice : Control {
        internal string Glyph, Detail, Badge;
        internal bool Current;
        internal DeskDevice() {SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=g.DpiX/96F;
            using(var p=DeskTheme.Round(LayoutGeometry.StrokeBounds(Width,Height,s),11*s))using(var b=new SolidBrush(Color.White))using(var pen=new Pen(Current?ColorTranslator.FromHtml("#c8beef"):DeskTheme.Line,s)){g.FillPath(b,p);g.DrawPath(pen,p);}
            using(var p=DeskTheme.Round(new RectangleF(15*s,(Height-41*s)/2,41*s,41*s),9*s))using(var b=new SolidBrush(DeskTheme.Sidebar))g.FillPath(b,p);
            DeskTheme.Icon(g,Glyph,ColorTranslator.FromHtml("#7e8594"),new RectangleF(22*s,(Height-27*s)/2,27*s,27*s));
            int left=(int)(68*s),badgeWidth=(int)(72*s),textWidth=Math.Max(10,Width-left-badgeWidth-(int)(20*s));
            using(var title=DeskTheme.Font(13,true))using(var small=DeskTheme.Font(10,false)) {
                DeskTheme.Text(g,Text,title,DeskTheme.Ink,new Rectangle(left,(int)(17*s),textWidth,(int)(22*s)),TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
                DeskTheme.Text(g,Detail,small,DeskTheme.Muted,new Rectangle(left,(int)(42*s),textWidth,(int)(18*s)),TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
                var r=new Rectangle(Width-badgeWidth-(int)(12*s),(Height-(int)(24*s))/2,badgeWidth,(int)(24*s));
                using(var p=DeskTheme.Round(r,5*s))using(var b=new SolidBrush(Current?DeskTheme.Soft:ColorTranslator.FromHtml("#f2f6f5")))g.FillPath(b,p);
                DeskTheme.Text(g,Badge,small,Current?DeskTheme.Violet:DeskTheme.Green,r,TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter);
            }
        }
    }

    internal sealed class DeskMonitorArt : Panel {
        internal DeskMonitorArt() {SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=g.DpiX/96F,cx=Width/2F;
            using(var p=new Pen(DeskTheme.HeroLine,s)){g.DrawEllipse(p,cx-108*s,6*s,216*s,216*s);g.DrawEllipse(p,cx-90*s,24*s,180*s,180*s);}
            using(var b=new SolidBrush(ColorTranslator.FromHtml("#b7b4c9")))g.FillRectangle(b,cx-15*s,183*s,30*s,14*s);
            using(var p=DeskTheme.Round(new RectangleF(cx-44*s,196*s,88*s,5*s),2*s))using(var b=new SolidBrush(ColorTranslator.FromHtml("#bbb8cc")))g.FillPath(b,p);
        }
    }
}
