using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MemoPad
{
    // ───────────────────────── 字体（内置霞鹜文楷） ─────────────────────────
    public static class Fonts
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern int AddFontResourceEx(string path, uint fl, IntPtr res);

        static PrivateFontCollection pfc;
        public static FontFamily Family;                 // 私有字体（Windows）
        public static string Face = "Microsoft YaHei UI"; // GDI / RTF 用的字体名
        public const string WenKai = "LXGW WenKai";

        static bool Win { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        public static void Load(string choice)
        {
            if (choice == "yahei") return;
            try
            {
                if (!Win)
                {
                    using (var f = new Font(WenKai, 10f)) if (f.Name == WenKai) Face = WenKai;
                    return;
                }
                var dir = Path.Combine(Store.Dir, "fonts");
                Directory.CreateDirectory(dir);
                pfc = new PrivateFontCollection();
                foreach (var w in new[] { "Regular", "Bold" })
                {
                    var path = Path.Combine(dir, "LXGWWenKai-" + w + ".ttf");
                    if (!File.Exists(path) || new FileInfo(path).Length < 1000000) Extract("font_" + w, path);
                    AddFontResourceEx(path, 0x10 /* FR_PRIVATE */, IntPtr.Zero);
                    pfc.AddFontFile(path);
                }
                Family = pfc.Families.FirstOrDefault(x => x.Name == WenKai) ?? pfc.Families.FirstOrDefault();
                if (Family != null) Face = Family.Name;
            }
            catch { Family = null; Face = "Microsoft YaHei UI"; }
        }

        static void Extract(string res, string path)
        {
            var tmp = path + ".tmp";
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
            using (var gz = new GZipStream(s, CompressionMode.Decompress))
            using (var o = File.Create(tmp)) gz.CopyTo(o);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static Font Make(float size, FontStyle st)
        {
            try
            {
                if (Family != null)
                {
                    if (Family.IsStyleAvailable(st)) return new Font(Family, size, st);
                    var basic = st & (FontStyle.Underline | FontStyle.Strikeout);
                    if (Family.IsStyleAvailable(basic)) return new Font(Family, size, basic);
                }
                return new Font(Face, size, st);
            }
            catch { return new Font("Microsoft YaHei UI", size, st); }
        }
    }

    // ───────────────────────── 纸本风格 ─────────────────────────
    public static class UI
    {
        public static float Dpi = 96f;
        public static int S(int v) { return (int)Math.Round(v * Dpi / 96f); }
        public static string FontName { get { return Fonts.Face; } }
        public static Font F(float size, FontStyle st = FontStyle.Regular) { return Fonts.Make(size, st); }

        public static readonly Color Bg = Color.FromArgb(247, 242, 232);        // 纸
        public static readonly Color Side = Color.FromArgb(239, 231, 216);      // 抽屉 / 顶栏
        public static readonly Color SideSel = Color.FromArgb(228, 216, 194);
        public static readonly Color Selection = Color.FromArgb(239, 228, 207);
        public static readonly Color Frame = Color.FromArgb(214, 200, 176);     // 边框
        public static readonly Color Line = Color.FromArgb(231, 220, 201);      // 横线
        public static readonly Color Ink = Color.FromArgb(58, 47, 37);          // 墨
        public static readonly Color Muted = Color.FromArgb(151, 136, 115);
        public static readonly Color MemoColor = Color.FromArgb(180, 71, 47);   // 朱
        public static readonly Color NoteColor = Color.FromArgb(63, 97, 126);   // 靛
        public static readonly Color Link = Color.FromArgb(63, 97, 126);
        public static readonly Color Seal = Color.FromArgb(178, 58, 43);

        public static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 菜单打开时，边缘浮层不收起
        public static int MenusOpen;
        public static void Track(ToolStripDropDown dd)
        {
            dd.Opened += (s, e) => MenusOpen++;
            dd.Closed += (s, e) => MenusOpen = Math.Max(0, MenusOpen - 1);
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { Font = F(10.5f), ShowImageMargin = true };
            Track(m);
            return m;
        }

        public static Button TextButton(string text, Color fore, Color back)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, Font = F(10.5f), ForeColor = fore, BackColor = back,
                Cursor = Cursors.Hand, TabStop = false, UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = SideSel;
            b.FlatAppearance.MouseDownBackColor = SideSel;
            return b;
        }
    }

    public class PaperColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return UI.SideSel; } }
        public override Color MenuItemBorder { get { return UI.SideSel; } }
        public override Color MenuBorder { get { return UI.Frame; } }
        public override Color MenuItemSelectedGradientBegin { get { return UI.SideSel; } }
        public override Color MenuItemSelectedGradientEnd { get { return UI.SideSel; } }
        public override Color MenuItemPressedGradientBegin { get { return UI.SideSel; } }
        public override Color MenuItemPressedGradientMiddle { get { return UI.SideSel; } }
        public override Color MenuItemPressedGradientEnd { get { return UI.SideSel; } }
        public override Color ToolStripDropDownBackground { get { return UI.Bg; } }
        public override Color ImageMarginGradientBegin { get { return UI.Bg; } }
        public override Color ImageMarginGradientMiddle { get { return UI.Bg; } }
        public override Color ImageMarginGradientEnd { get { return UI.Bg; } }
        public override Color ButtonSelectedHighlight { get { return UI.SideSel; } }
        public override Color ButtonSelectedBorder { get { return UI.SideSel; } }
        public override Color ButtonSelectedGradientBegin { get { return UI.SideSel; } }
        public override Color ButtonSelectedGradientMiddle { get { return UI.SideSel; } }
        public override Color ButtonSelectedGradientEnd { get { return UI.SideSel; } }
        public override Color ButtonPressedGradientBegin { get { return UI.Frame; } }
        public override Color ButtonPressedGradientMiddle { get { return UI.Frame; } }
        public override Color ButtonPressedGradientEnd { get { return UI.Frame; } }
        public override Color ButtonCheckedGradientBegin { get { return UI.SideSel; } }
        public override Color ButtonCheckedGradientMiddle { get { return UI.SideSel; } }
        public override Color ButtonCheckedGradientEnd { get { return UI.SideSel; } }
        public override Color CheckBackground { get { return UI.SideSel; } }
        public override Color CheckSelectedBackground { get { return UI.Frame; } }
        public override Color CheckPressedBackground { get { return UI.Frame; } }
        public override Color SeparatorDark { get { return UI.Frame; } }
        public override Color SeparatorLight { get { return UI.Bg; } }
        public override Color ToolStripBorder { get { return UI.Side; } }
        public override Color ToolStripGradientBegin { get { return UI.Side; } }
        public override Color ToolStripGradientMiddle { get { return UI.Side; } }
        public override Color ToolStripGradientEnd { get { return UI.Side; } }
        public override Color OverflowButtonGradientBegin { get { return UI.Side; } }
        public override Color OverflowButtonGradientMiddle { get { return UI.Side; } }
        public override Color OverflowButtonGradientEnd { get { return UI.Side; } }
    }

    public class PaperRenderer : ToolStripProfessionalRenderer
    {
        public PaperRenderer() : base(new PaperColors()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) { base.OnRenderToolStripBackground(e); return; }
            using (var b = new SolidBrush(e.ToolStrip.BackColor)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Enabled && e.Item.ForeColor == SystemColors.ControlText) e.TextColor = UI.Ink;
            base.OnRenderItemText(e);
        }
    }
}
