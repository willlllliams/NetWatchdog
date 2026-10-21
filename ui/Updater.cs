// Update check against GitHub Releases.
// The latest release is read from https://api.github.com/repos/<AppInfo.Repo>/releases/latest.
// A release counts as an update when its tag (v1.2.3) is higher than AppInfo.Version. The installer
// asset must be named NetWatchdog-Setup-<version>.exe, and its SHA-256 must be known: GitHub's asset
// "digest" field is used when present, otherwise a "SHA-256: <hex>" line in the release notes.
// A download whose size or hash does not match is deleted and never run.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace NetWatchdogUI
{
    class ReleaseInfo
    {
        public Version Version;
        public string Tag, Page, AssetName, AssetUrl, Sha256;
        public long Size;
    }

    static class Updater
    {
        const string SettingsKey = @"Software\NetWatchdog";

        // Overridable with --update-url for testing (a file:// URL to a JSON file works too)
        public static string ApiUrl = "https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest";

        public static string ReleasesPage { get { return "https://github.com/" + AppInfo.Repo + "/releases"; } }

        public static bool Enabled
        {
            get { return ReadInt("CheckUpdates", 1) != 0; }
            set { WriteValue("CheckUpdates", value ? 1 : 0, RegistryValueKind.DWord); }
        }

        public static DateTime LastCheck
        {
            get
            {
                DateTime t;
                string s = ReadString("LastUpdateCheck");
                return s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t : DateTime.MinValue;
            }
            set { WriteValue("LastUpdateCheck", value.ToString("o"), RegistryValueKind.String); }
        }

        static WebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = WebRequest.Create(url);
            req.Timeout = 20000;
            var http = req as HttpWebRequest;
            if (http != null)
            {
                http.UserAgent = "NetWatchdog/" + AppInfo.Version;
                http.Accept = "application/vnd.github+json";
                http.ReadWriteTimeout = 20000;
            }
            return req;
        }

        // Returns the latest release, or null when the repository has no release yet
        public static ReleaseInfo FetchLatest()
        {
            string json;
            try
            {
                using (var resp = Request(ApiUrl).GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }

            var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) throw new InvalidDataException("Unexpected release data");
            string tag = Str(d, "tag_name") ?? "";
            Version version;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out version)) throw new InvalidDataException("Unrecognized release tag " + tag);

            var r = new ReleaseInfo { Version = version, Tag = tag, Page = Str(d, "html_url") ?? ReleasesPage };
            object assets;
            if (d.TryGetValue("assets", out assets) && assets is object[])
            {
                foreach (var a in ((object[])assets).OfType<Dictionary<string, object>>())
                {
                    string name = Str(a, "name") ?? "";
                    if (!Regex.IsMatch(name, @"^NetWatchdog-Setup-[\d.]+\.exe$", RegexOptions.IgnoreCase)) continue;
                    r.AssetName = name;
                    r.AssetUrl = Str(a, "browser_download_url");
                    long size;
                    if (long.TryParse(Str(a, "size"), out size)) r.Size = size;
                    string digest = Str(a, "digest");
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) r.Sha256 = digest.Substring(7);
                    break;
                }
            }
            if (r.Sha256 == null)
            {
                var m = Regex.Match(Str(d, "body") ?? "", @"SHA-?256\s*[:：]?\s*`?([0-9a-fA-F]{64})");
                if (m.Success) r.Sha256 = m.Groups[1].Value;
            }
            if (r.Sha256 != null) r.Sha256 = r.Sha256.ToLowerInvariant();
            return r;
        }

        public static bool IsNewer(ReleaseInfo r)
        {
            return r != null && r.Version > Version.Parse(AppInfo.Version);
        }

        // Download the installer into %TEMP%\NetWatchdog-update and verify size and SHA-256.
        // Runs on a background thread; progress reports 0-100.
        public static string Download(ReleaseInfo r, Action<int> progress)
        {
            if (r.AssetUrl == null) throw new InvalidOperationException(L.T("The release has no installer attached.", "这个版本没有附带安装包。"));
            if (r.Sha256 == null) throw new InvalidOperationException(L.T("The release does not publish a SHA-256 hash, so the download cannot be verified.", "这个版本没有公布 SHA-256 校验值，无法校验下载的文件。"));

            string dir = Path.Combine(Path.GetTempPath(), "NetWatchdog-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Path.GetFileName(r.AssetName));
            string tmp = path + ".part";

            using (var resp = Request(r.AssetUrl).GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(tmp))
            {
                long total = r.Size > 0 ? r.Size : resp.ContentLength;
                var buf = new byte[81920];
                long done = 0;
                int last = -1, n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != last) { last = pct; progress(pct); }
                }
            }

            long actual = new FileInfo(tmp).Length;
            if (r.Size > 0 && actual != r.Size)
            {
                File.Delete(tmp);
                throw new InvalidDataException(L.F("The download is {0} bytes, expected {1}.", "下载的文件大小为 {0} 字节，应为 {1} 字节。", actual, r.Size));
            }
            string hash = Sha256Of(tmp);
            if (hash != r.Sha256)
            {
                File.Delete(tmp);
                throw new InvalidDataException(L.T("The SHA-256 of the download does not match the release; the file was deleted.", "下载文件的 SHA-256 与发布信息不一致，已删除该文件。"));
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static int ReadInt(string name, int fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }

        static string ReadString(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    return k == null ? null : k.GetValue(name) as string;
            }
            catch { return null; }
        }

        static void WriteValue(string name, object value, RegistryValueKind kind)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue(name, value, kind);
            }
            catch { }
        }
    }
}
