// AI repair: helpers for the Pi coding agent that the watchdog starts as SYSTEM during an outage.
// Pi lives in <install dir>\pi and its configuration in %ProgramData%\NetWatchdog\ai\agent; both are
// writable only by administrators, so the elevated helpers below are the only way to change them.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NetWatchdogUI
{
    static class AiSetup
    {
        public const string Provider = "netwatchdog";
        public const string PiPackage = "@earendil-works/pi-coding-agent";
        public static readonly Version MinNode = new Version(22, 19);

        public static string DataDir { get { return AppPaths.DataDir; } }
        public static string AiDir { get { return Path.Combine(DataDir, "ai"); } }
        public static string AgentDir { get { return Path.Combine(AiDir, "agent"); } }
        public static string PromptPath { get { return Path.Combine(AiDir, "prompt.md"); } }
        public const int MaxPrompt = 4000;

        // Presets: service -> OpenAI-compatible base URL
        public static readonly Dictionary<string, string> Presets = new Dictionary<string, string>
        {
            { "ollama", "http://localhost:11434/v1" },
            { "lmstudio", "http://localhost:1234/v1" },
            { "llamacpp", "http://localhost:8080/v1" },
        };

        public static string DetectPreset(string url)
        {
            foreach (var kv in Presets)
                if (string.Equals((url ?? "").TrimEnd('/'), kv.Value, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return "custom";
        }

        public static bool ValidUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 300 || url.Any(c => char.IsWhiteSpace(c) || c == '"')) return false;
            // No user name or password (they would end up in files every user can read), no query or fragment
            // (the API paths are appended to the address)
            return Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")
                && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0;
        }

        public static bool ValidModel(string model)
        {
            return !string.IsNullOrEmpty(model) && model.Length <= 200 && !model.Any(c => char.IsControl(c) || c == '"');
        }

        // During an outage only this computer and the local network can be reached
        public static bool IsLocal(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            string h = u.Host.Trim('[', ']').ToLowerInvariant();
            IPAddress ip;
            if (!IPAddress.TryParse(h, out ip))
                return h == "localhost" || h.EndsWith(".local") || h.EndsWith(".lan") || h.EndsWith(".home.arpa") || !h.Contains('.');
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (b.Length == 4) return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xfe) == 0xfc;
        }

        public static Version ParseVersion(string s)
        {
            Version v;
            return Version.TryParse((s ?? "").TrimStart('v'), out v) ? v : null;
        }

        // GET <base>/models; returns the model IDs. Throws on network or protocol errors.
        public static List<string> ListModels(string baseUrl, string key)
        {
            var req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/models");
            req.Timeout = 6000;
            req.ReadWriteTimeout = 6000;
            if (IsLocal(baseUrl)) req.Proxy = null;
            if (!string.IsNullOrEmpty(key)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            string text;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = sr.ReadToEnd();
            var d = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (d == null) throw new InvalidDataException(L.T("The answer is not a model list", "返回的内容不是模型列表"));
            object data;
            var list = new List<string>();
            if (d.TryGetValue("data", out data) && data is object[])
                foreach (var m in (object[])data)
                {
                    var md = m as Dictionary<string, object>;
                    object id;
                    if (md != null && md.TryGetValue("id", out id) && id != null) list.Add(Convert.ToString(id, CultureInfo.InvariantCulture));
                }
            return list;
        }

        // ------------------------------------------------ Elevated: --save-ai <json file> <sha256>

        // Input { baseUrl, model, apiKey, keepKey } and/or { prompt }. The panel passes the SHA-256 of the file it
        // wrote; the file sits in the user's temp folder while the UAC prompt is open, so another program could
        // replace it, and the helper only uses content whose hash matches. keepKey keeps the saved key when the
        // key box was left empty.
        // The service goes into the provider "netwatchdog" in ai\agent\models.json, keeping anything else in that
        // file, and { baseUrl, model, hasKey } into ai\ai.json, which the watchdog and the panel read. The key stays
        // in the agent folder, which only SYSTEM and administrators can read.
        // The prompt goes into ai\prompt.md (empty deletes it); like everything the AI runs with, only
        // administrators can change it, otherwise any program could tell the AI what to do.
        public static int Save(string input, string sha256)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                byte[] bytes = File.ReadAllBytes(input);
                if (!string.Equals(HashOf(bytes), sha256, StringComparison.OrdinalIgnoreCase)) return 3;
                var d = ser.DeserializeObject(new UTF8Encoding(false).GetString(bytes)) as Dictionary<string, object>;
                if (d == null) return 2;
                Directory.CreateDirectory(AiDir);
                if (d.ContainsKey("prompt"))
                {
                    string prompt = Get(d, "prompt").Replace("\r\n", "\n").Trim();
                    if (prompt.Length > MaxPrompt || prompt.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) return 2;
                    if (prompt.Length == 0) { if (File.Exists(PromptPath)) File.Delete(PromptPath); }
                    else AppPaths.WriteAtomic(PromptPath, prompt + "\n");
                }
                if (!d.ContainsKey("baseUrl")) return 0;
                string url = Get(d, "baseUrl").Trim().TrimEnd('/'), model = Get(d, "model").Trim(), key = Get(d, "apiKey").Trim();
                if (!ValidUrl(url) || !ValidModel(model) || key.Length > 1000 || key.Any(char.IsControl)) return 2;

                // The key must never land in a folder other users can read: give up when the permissions are not set
                Directory.CreateDirectory(AgentDir);
                int acl = Exec("icacls.exe", "\"" + AgentDir + "\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /Q");
                if (acl != 0 || !OnlyAdmins(AgentDir)) return 4;

                string modelsPath = Path.Combine(AgentDir, "models.json");
                var root = new Dictionary<string, object>();
                if (File.Exists(modelsPath))
                {
                    try { root = ser.DeserializeObject(File.ReadAllText(modelsPath, Encoding.UTF8)) as Dictionary<string, object> ?? root; }
                    catch { root = new Dictionary<string, object>(); }
                }
                object po;
                var providers = root.TryGetValue("providers", out po) ? po as Dictionary<string, object> : null;
                if (providers == null) { providers = new Dictionary<string, object>(); root["providers"] = providers; }
                // Pi reads "$NAME" as an environment variable and a leading "!" as a command; escape both
                string literal = key.Length == 0 ? Provider : key.Replace("$", "$$");
                if (literal.StartsWith("!")) literal = "$" + literal;
                bool hasKey = key.Length > 0;
                object kk, old, oldKey;
                if (key.Length == 0 && d.TryGetValue("keepKey", out kk) && kk is bool && (bool)kk
                    && providers.TryGetValue(Provider, out old) && old is Dictionary<string, object>
                    && ((Dictionary<string, object>)old).TryGetValue("apiKey", out oldKey) && oldKey is string && (string)oldKey != Provider)
                {
                    literal = (string)oldKey;
                    hasKey = true;
                }
                providers[Provider] = new Dictionary<string, object>
                {
                    { "name", "NetWatchdog" },
                    { "baseUrl", url },
                    { "api", "openai-completions" },
                    { "apiKey", literal },
                    { "models", new object[] { new Dictionary<string, object> { { "id", model } } } },
                };
                AppPaths.WriteAtomic(modelsPath, Json.Write(root));
                AppPaths.WriteAtomic(Path.Combine(AiDir, "ai.json"), Json.Write(new Dictionary<string, object>
                {
                    { "baseUrl", url }, { "model", model }, { "hasKey", hasKey }, { "saved", DateTime.Now.ToString("o") },
                }));
                return 0;
            }
            catch (Exception ex) { Program.LogError(ex); return 1; }
        }

        static string Get(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        public static string HashOf(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        // Exit code, or -1 when the program did not start or did not finish within 30 s
        static int Exec(string file, string args)
        {
            using (var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false }))
            {
                if (p == null || !p.WaitForExit(30000)) return -1;
                return p.ExitCode;
            }
        }

        // True when no one but SYSTEM and Administrators is granted anything on the folder
        static bool OnlyAdmins(string dir)
        {
            var allowed = new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
            var rules = Directory.GetAccessControl(dir).GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule r in rules)
                if (r.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)r.IdentityReference)) return false;
            return true;
        }

        // ------------------------------------------------ --ai-reminder

        // Started from the Run key at sign-in while AI repair mode is on: a one-second note above the tray
        public static int Reminder(string statusPath)
        {
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(statusPath, Encoding.UTF8)) as Dictionary<string, object>;
                object ao, on;
                var ai = d != null && d.TryGetValue("ai", out ao) ? ao as Dictionary<string, object> : null;
                // The language was set by Main from the same status file
                if (ai == null || !ai.TryGetValue("on", out on) || !(on is bool) || !(bool)on) return 0;
            }
            catch { return 0; }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = Theme.Load(null);
            var text = new TextBlock
            {
                Text = L.T("AI repair mode is on", "AI 修复模式处于开启状态"),
                FontSize = 14, Foreground = theme["Text"], VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
                Foreground = theme["Bad"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(text);
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
                Content = new Border
                {
                    Background = theme["Card"], BorderBrush = theme["Bad"], BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 18, 12), Child = row,
                },
            };
            w.Loaded += (s, e) =>
            {
                var area = SystemParameters.WorkArea;
                w.Left = area.Right - w.ActualWidth - 12;
                w.Top = area.Bottom - w.ActualHeight - 12;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s2, e2) =>
                {
                    timer.Stop();
                    var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                    fade.Completed += (s3, e3) => app.Shutdown();
                    w.BeginAnimation(UIElement.OpacityProperty, fade);
                };
                timer.Start();
            };
            L.Apply(w);
            w.Left = -32000;
            w.Show();
            app.Run();
            return 0;
        }
    }

    // Indented JSON for files people may open (models.json), with keys in insertion order
    static class Json
    {
        public static string Write(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int depth)
        {
            string pad = new string(' ', (depth + 1) * 2), end = new string(' ', depth * 2);
            if (o == null) sb.Append("null");
            else if (o is bool) sb.Append((bool)o ? "true" : "false");
            else if (o is string) Str(sb, (string)o);
            else if (o is int || o is long || o is decimal || o is double) sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
            else if (o is IDictionary<string, object>)
            {
                var d = (IDictionary<string, object>)o;
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in d)
                {
                    sb.Append(pad);
                    Str(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value, depth + 1);
                    sb.Append(++i < d.Count ? ",\n" : "\n");
                }
                sb.Append(end).Append('}');
            }
            else if (o is IEnumerable)
            {
                var items = ((IEnumerable)o).Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(pad);
                    Write(sb, items[i], depth + 1);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(end).Append(']');
            }
            else Str(sb, Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
