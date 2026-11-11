using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace MemoPad
{
    // 自动更新：把新版 MemoPad.exe 放进 %AppData%\MemoPad\update\ ，
    // 程序发现版本更新就替换自己并重启。
    public static class Updater
    {
        public static string Dir { get { return Path.Combine(Store.Dir, "update"); } }
        public static string Pending { get { return Path.Combine(Dir, "MemoPad.exe"); } }
        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        public static void CleanupOld()
        {
            try { Directory.CreateDirectory(Dir); } catch { }
            try
            {
                var old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }

        static Version VersionOf(string path)
        {
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(path).FileVersion;
                Version v;
                if (!string.IsNullOrEmpty(fv) && Version.TryParse(fv, out v)) return v;
            }
            catch { }
            return null;
        }

        // 有可用的新版本？
        public static bool HasUpdate()
        {
            if (!File.Exists(Pending)) return false;
            try { if (new FileInfo(Pending).Length < 100000) return false; } catch { return false; }  // 还在写入
            var v = VersionOf(Pending);
            if (v == null) return false;
            if (v <= Current) { try { File.Delete(Pending); } catch { } return false; }   // 旧的，丢掉
            return true;
        }

        // 替换自己，返回 true 表示已经安排好重启
        public static bool Apply()
        {
            var exe = Application.ExecutablePath;
            var old = exe + ".old";
            var v = VersionOf(Pending);
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);                 // 运行中的 exe 可以改名
                try { File.Copy(Pending, exe); }
                catch { File.Move(old, exe); throw; } // 失败就还原
                try { File.Delete(Pending); } catch { }
                File.WriteAllText(Path.Combine(Store.Dir, "updated.txt"), v != null ? v.ToString() : "");
                Program.Trace("updated to " + v);
                Process.Start(exe, "--restart");
                return true;
            }
            catch (Exception ex)
            {
                Program.Trace("update failed: " + ex.Message);
                return false;
            }
        }

        // ── 联网检查 ──
        // 仓库里放 version.txt（第一行版本号，第二行 exe 的 SHA256）和 MemoPad.exe，并打 tag v版本号
        const string Repo = "as1018518103-hue/memopad";
        static readonly string[] VersionUrls = {
            "https://raw.githubusercontent.com/" + Repo + "/main/version.txt",
            "https://cdn.jsdelivr.net/gh/" + Repo + "@main/version.txt",
            "https://fastly.jsdelivr.net/gh/" + Repo + "@main/version.txt",
        };
        static string[] ExeUrls(string tag)
        {
            return new[] {
                "https://raw.githubusercontent.com/" + Repo + "/" + tag + "/MemoPad.exe",
                "https://cdn.jsdelivr.net/gh/" + Repo + "@" + tag + "/MemoPad.exe",
                "https://fastly.jsdelivr.net/gh/" + Repo + "@" + tag + "/MemoPad.exe",
                "https://github.com/" + Repo + "/raw/" + tag + "/MemoPad.exe",
            };
        }

        static bool checking;
        public static string LastStatus = "";

        // 后台检查并下载；下载好后放进 update 文件夹，由 HasUpdate/Apply 接手
        public static void CheckOnlineAsync(Action<string> done = null)
        {
            if (checking) return;
            checking = true;
            var t = new System.Threading.Thread(() =>
            {
                string result;
                try { result = CheckOnline(); }
                catch (Exception ex) { result = "检查失败：" + ex.Message; }
                LastStatus = result;
                Program.Trace("online check: " + result);
                checking = false;
                if (done != null) done(result);
            }) { IsBackground = true };
            t.Start();
        }

        static System.Net.WebClient Client()
        {
            var wc = new System.Net.WebClient();
            wc.Headers.Add("User-Agent", "MemoPad/" + Current);
            wc.Headers.Add("Cache-Control", "no-cache");
            return wc;
        }

        static string CheckOnline()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch { }  // TLS 1.2

            string text = null;
            var testBase = Environment.GetEnvironmentVariable("MEMOPAD_UPDATE_BASE");   // 仅测试用
            var vurls = testBase != null ? new[] { testBase + "/main/version.txt" } : VersionUrls;
            foreach (var u in vurls)
            {
                try { using (var wc = Client()) { text = wc.DownloadString(u + "?t=" + DateTime.UtcNow.Ticks); } if (!string.IsNullOrWhiteSpace(text)) break; }
                catch { text = null; }
            }
            if (text == null) return "连不上更新服务器";

            var lines = text.Replace("\r", "").Split('\n');
            Version remote;
            if (!Version.TryParse(lines[0].Trim(), out remote)) return "版本信息无效";
            string sha = lines.Length > 1 ? lines[1].Trim().ToLowerInvariant() : "";
            if (remote <= Current) return "已经是最新版 v" + Current.ToString(3);
            if (File.Exists(Pending) && VersionOf(Pending) >= remote) return "新版已下载";

            Directory.CreateDirectory(Dir);
            var tmp = Pending + ".download";
            string tag = "v" + remote.ToString(3);
            foreach (var u in testBase != null ? new[] { testBase + "/" + tag + "/MemoPad.exe" } : ExeUrls(tag))
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    using (var wc = Client()) wc.DownloadFile(u, tmp);
                    if (sha.Length == 64 && Sha256(tmp) != sha) { Program.Trace("sha mismatch from " + u); continue; }
                    var v = VersionOf(tmp);
                    if (v == null || v < remote) { Program.Trace("bad version from " + u); continue; }
                    if (File.Exists(Pending)) File.Delete(Pending);
                    File.Move(tmp, Pending);
                    return "已下载 v" + remote.ToString(3);
                }
                catch (Exception ex) { Program.Trace("download failed " + u + ": " + ex.Message); }
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return "下载新版失败";
        }

        static string Sha256(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // 刚更新完？返回新版本号（只返回一次）
        public static string JustUpdated()
        {
            var f = Path.Combine(Store.Dir, "updated.txt");
            if (!File.Exists(f)) return null;
            try { var s = File.ReadAllText(f).Trim(); File.Delete(f); return s; } catch { return null; }
        }
    }
}
