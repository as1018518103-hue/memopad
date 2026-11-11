// MemoPad 便签本 — 备忘录 + 笔记，开机启动，可互相链接
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("MemoPad 便签本")]
[assembly: System.Reflection.AssemblyProduct("MemoPad")]
[assembly: System.Reflection.AssemblyVersion("1.6.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.6.0.0")]

namespace MemoPad
{
    // ───────────────────────── 数据 ─────────────────────────
    [DataContract]
    public class PageInfo
    {
        [DataMember] public string Id;
        [DataMember] public string Title;
        [DataMember] public string Type;      // "memo" | "note"
        [DataMember] public DateTime Created;
        [DataMember] public DateTime Modified;
        public bool IsMemo { get { return Type == "memo"; } }
    }

    [DataContract]
    public class MemoItem
    {
        [DataMember] public string Text;
        [DataMember] public bool Done;
        [DataMember] public string LinkId;
        [DataMember] public DateTime Created;
    }

    [DataContract]
    public class MemoData
    {
        [DataMember] public List<MemoItem> Items;
    }

    [DataContract]
    public class AppState
    {
        [DataMember] public List<PageInfo> Pages;
        [DataMember] public string LastPageId;
        [DataMember] public int X, Y, W, H, Split;
        [DataMember] public bool Maximized;
        [DataMember] public bool TopMost;
        [DataMember] public bool Locked;
        [DataMember] public string FontChoice;
        [DataMember] public bool HideDone;
        [DataMember] public bool TrayTipShown;
    }

    public static class Store
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoPad");

        public static string StatePath { get { return Path.Combine(Dir, "pages.json"); } }
        public static string MemoPath(string id) { return Path.Combine(Dir, "memo_" + id + ".json"); }
        public static string NotePath(string id) { return Path.Combine(Dir, "note_" + id + ".rtf"); }

        public static void Init() { Directory.CreateDirectory(Dir); }

        public static T ReadJson<T>(string path) where T : class
        {
            foreach (var p in new[] { path, path + ".bak" })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    using (var fs = File.OpenRead(p))
                        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(fs);
                }
                catch { }
            }
            return null;
        }

        public static void WriteJson<T>(string path, T obj)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, obj);
                WriteBytes(path, ms.ToArray());
            }
        }

        public static string ReadText(string path)
        {
            if (File.Exists(path)) try { return File.ReadAllText(path, Encoding.UTF8); } catch { }
            if (File.Exists(path + ".bak")) try { return File.ReadAllText(path + ".bak", Encoding.UTF8); } catch { }
            return null;
        }

        public static void WriteText(string path, string text) { WriteBytes(path, Encoding.UTF8.GetBytes(text ?? "")); }

        static void WriteBytes(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); } catch { }
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        public static void DeletePage(PageInfo p)
        {
            foreach (var f in new[] { MemoPath(p.Id), NotePath(p.Id) })
                foreach (var g in new[] { f, f + ".bak" })
                    try { if (File.Exists(g)) File.Delete(g); } catch { }
        }
    }

    // 手写 RTF（保证中文正确）
    public class RtfBuilder
    {
        readonly List<Color> colors = new List<Color>();
        readonly StringBuilder body = new StringBuilder();
        public void Add(string text, float size, FontStyle st, Color c)
        {
            int ci = colors.IndexOf(c); if (ci < 0) { colors.Add(c); ci = colors.Count - 1; }
            body.Append("{\\f0\\fs").Append((int)(size * 2)).Append("\\cf").Append(ci + 1);
            if (st.HasFlag(FontStyle.Bold)) body.Append("\\b");
            if (st.HasFlag(FontStyle.Italic)) body.Append("\\i");
            if (st.HasFlag(FontStyle.Underline)) body.Append("\\ul");
            body.Append(' ');
            foreach (char ch in text)
            {
                if (ch == '\n') body.Append("\\par\n");
                else if (ch == '\\' || ch == '{' || ch == '}') body.Append('\\').Append(ch);
                else if (ch < 128) body.Append(ch);
                else body.Append("\\u").Append((int)(short)ch).Append('?');
            }
            body.Append('}');
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("{\\rtf1\\ansi\\ansicpg936\\deff0{\\fonttbl{\\f0\\fnil\\fcharset134 " + Fonts.Face + ";}}{\\colortbl ;");
            foreach (var c in colors) sb.AppendFormat("\\red{0}\\green{1}\\blue{2};", c.R, c.G, c.B);
            sb.Append("}\n\\pard\\sa60 ").Append(body).Append("}");
            return sb.ToString();
        }
    }


    // ───────────────────────── 桌面层 ─────────────────────────
    // 窗口挂在桌面 (Progman) 下：Win+D「显示桌面」时它仍然在；
    // 平时待在所有窗口最下面，不置顶；永远不会最小化。
    public static class Desktop
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        static readonly IntPtr HWND_BOTTOM = (IntPtr)1, HWND_TOP = IntPtr.Zero;
        const uint NOSIZE = 0x1, NOMOVE = 0x2, NOACTIVATE = 0x10, SHOWWINDOW = 0x40;
        static bool Win { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        public static void AttachToDesktop(Form f)
        {
            if (!Win) return;
            try
            {
                var progman = FindWindow("Progman", null);
                if (progman == IntPtr.Zero) return;
                if (IntPtr.Size == 8) SetWindowLongPtr(f.Handle, -8, progman);   // GWLP_HWNDPARENT = 所有者
                else SetWindowLong(f.Handle, -8, progman.ToInt32());
            }
            catch { }
        }

        public static void SendToBottom(Form f)
        {
            if (!Win || !f.IsHandleCreated) return;
            SetWindowPos(f.Handle, HWND_BOTTOM, 0, 0, 0, 0, NOSIZE | NOMOVE | NOACTIVATE);
        }

        public static void BringUp(Form f)
        {
            if (!Win || !f.IsHandleCreated) return;
            SetWindowPos(f.Handle, HWND_TOP, 0, 0, 0, 0, NOSIZE | NOMOVE | SHOWWINDOW);
        }

        static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            return sb.ToString();
        }

        // 每 0.5 秒检查一次：被最小化/隐藏就恢复；回到桌面时确保自己浮在桌面上面
        public static void Guard(Form f)
        {
            if (!Win || !f.IsHandleCreated) return;
            var h = f.Handle;
            if (IsIconic(h) || !IsWindowVisible(h))
            {
                ShowWindow(h, 4);   // SW_SHOWNOACTIVATE
                SendToBottom(f);
                return;
            }
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            var c = ClassOf(fg);
            if (c == "WorkerW" || c == "Progman")
                SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, NOSIZE | NOMOVE | NOACTIVATE);
        }

        public static void BeginDrag(Form f)
        {
            if (!Win) return;
            ReleaseCapture();
            SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        }
    }

    // 备忘录数据缓存
    public static class MemoCache
    {
        static readonly Dictionary<string, MemoData> cache = new Dictionary<string, MemoData>();
        public static MemoData Get(string id)
        {
            MemoData d;
            if (cache.TryGetValue(id, out d)) return d;
            d = Store.ReadJson<MemoData>(Store.MemoPath(id)) ?? new MemoData();
            if (d.Items == null) d.Items = new List<MemoItem>();
            cache[id] = d;
            return d;
        }
        public static void Save(string id, MemoData d)
        {
            cache[id] = d;
            try { Store.WriteJson(Store.MemoPath(id), d); } catch { }
        }
        public static void Forget(string id) { cache.Remove(id); }
    }

}
