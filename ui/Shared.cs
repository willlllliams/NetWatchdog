// Code shared by the tray panel and the installer: UI language, system theme and colors,
// data-binding base class, XAML resource loading, Win32 interop and shortcuts.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xaml;
using Microsoft.Win32;

// Makes AppContext use the 4.8 defaults, which WPF needs to rescale correctly between monitors with different DPI
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyProduct("NetWatchdog")]
[assembly: AssemblyCompany("NetWatchdog")]
[assembly: AssemblyCopyright("NetWatchdog")]

namespace NetWatchdogUI
{
    // ---------------------------------------------------------------- Language

    // UI language of the tray panel and the installer.
    //
    // Strings are written once at the call site as an English/Chinese pair, L.T("English", "中文"). Simplified
    // Chinese ("zh") and English ("en") use the pair directly. Every other language looks the English text up
    // in its translation table, the embedded resource i18n/<code>.json (a JSON object: English source text ->
    // translation) and falls back to the English text when the table has no entry.
    //
    // To add a language: create ui/i18n/<code>.json, add its code, native name, font and culture to
    // L.Languages below, and add a table for the code to $Messages in NetWatchdog.ps1. build.ps1 embeds every
    // ui/i18n/*.json by itself; tools/Check-I18n.ps1 lists the strings a table still lacks.
    public static class L
    {
        public class Info
        {
            public string Code, Name, Culture, Font;
            public Info(string code, string name, string culture, string font) { Code = code; Name = name; Culture = culture; Font = font; }
        }

        const string BaseFont = "Segoe UI Variable Text, Segoe UI, ";

        // Codes as stored in the watchdog's "language" setting (besides "auto"), each language in its own name
        public static readonly Info[] Languages =
        {
            new Info("zh", "简体中文", "zh-CN", BaseFont + "Microsoft YaHei UI, Microsoft JhengHei UI, Yu Gothic UI, Malgun Gothic"),
            new Info("zh-TW", "繁體中文", "zh-TW", BaseFont + "Microsoft JhengHei UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"),
            new Info("en", "English", "en-US", BaseFont + "Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic"),
            new Info("ja", "日本語", "ja-JP", BaseFont + "Yu Gothic UI, Meiryo UI, Microsoft YaHei UI, Malgun Gothic"),
            new Info("ko", "한국어", "ko-KR", BaseFont + "Malgun Gothic, Yu Gothic UI, Microsoft YaHei UI"),
        };

        // Code of the language in use
        public static string Lang = "en";
        static Dictionary<string, string> table;

        static L() { Init("auto"); }

        // Simplified Chinese: the pair's Chinese text is used as written
        public static bool Zh { get { return Lang == "zh"; } }

        public static Info Current { get { return Languages.First(l => l.Code == Lang); } }

        // Maps a setting or culture name (auto, zh, zh-CN, zh-Hant, zh-TW, ja, ja-JP, ko, en-GB ...) to one of the
        // codes above; "auto" and empty mean the Windows display language. Returns null for an unknown language.
        public static string Normalize(string setting)
        {
            string s = (setting ?? "").Trim().ToLowerInvariant().Replace('_', '-');
            bool auto = s.Length == 0 || s == "auto";
            if (auto) s = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            if (s.StartsWith("zh"))
            {
                bool traditional = s.Contains("hant") || s.EndsWith("-tw") || s.EndsWith("-hk") || s.EndsWith("-mo") || s.EndsWith("-cht");
                return traditional ? "zh-TW" : "zh";
            }
            if (s == "ja" || s.StartsWith("ja-")) return "ja";
            if (s == "ko" || s.StartsWith("ko-")) return "ko";
            if (s == "en" || s.StartsWith("en-")) return "en";
            // A Windows display language without a translation shows English; an unknown setting is ignored
            return auto ? "en" : null;
        }

        // Switches the language: "auto" follows Windows, an unknown code leaves the language as it is
        public static void Init(string setting)
        {
            string code = Normalize(setting);
            if (code == null) return;
            Lang = code;
            table = Load(code);
        }

        static Dictionary<string, string> Load(string code)
        {
            if (code == "zh" || code == "en") return null;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetWatchdogUI.i18n." + code + ".json"))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    {
                        var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
                        if (d == null) return null;
                        var t = new Dictionary<string, string>(d.Count);
                        foreach (var kv in d)
                        {
                            var v = kv.Value as string;
                            if (!string.IsNullOrEmpty(v)) t[kv.Key] = code == "ko" ? KeepWords(v) : v;
                        }
                        return t;
                    }
                }
            }
            catch { return null; }
        }

        // WPF may break a line between any two Hangul syllables, in the middle of a word. A word joiner (U+2060,
        // invisible) between syllables leaves the spaces as the only places where Korean text wraps.
        static string KeepWords(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s, "(?<=[\uAC00-\uD7A3])(?=[\uAC00-\uD7A3])", "\u2060");
        }

        public static string T(string en, string zh)
        {
            if (Lang == "zh") return zh;
            if (table == null) return en;
            string v;
            return table.TryGetValue(en, out v) ? v : en;
        }

        public static string F(string en, string zh, params object[] args) { return string.Format(T(en, zh), args); }

        // The phrase typed to confirm turning on AI repair mode, in the language in use
        public static string RiskPhrase { get { return L.T("I understand the risk", "我已了解风险").Replace("\u2060", ""); } }

        // Applies the language's fonts and culture to a window; Han characters get the right regional glyphs
        public static void Apply(FrameworkElement root)
        {
            var info = Current;
            root.Language = XmlLanguage.GetLanguage(info.Culture);
            root.SetValue(Control.FontFamilyProperty, new FontFamily(info.Font));
        }
    }

    // XAML counterpart of L.T: Text="{local:Tr 'Exit', '退出'}"
    [MarkupExtensionReturnType(typeof(string))]
    public class TrExtension : MarkupExtension
    {
        public TrExtension() { }
        public TrExtension(string en, string zh) { En = en; Zh = zh; }

        [ConstructorArgument("en")] public string En { get; set; }
        [ConstructorArgument("zh")] public string Zh { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) { return L.T(En, Zh); }
    }

    // ---------------------------------------------------------------- Theme

    // Follows the system (taskbar) light/dark mode unless the user picked light or dark, and the system accent
    // color; colors match Windows 11 Quick Settings
    class Theme
    {
        public bool Dark;
        public Color Accent;
        readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();

        // The user's choice, kept per user in HKCU\Software\NetWatchdog (value "Theme"): system, light or dark.
        // --dark and --light on the command line override it.
        const string SettingsKey = AppPaths.UserKey;

        public static string Mode
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    {
                        string v = k == null ? null : k.GetValue("Theme") as string;
                        if (v == "light" || v == "dark") return v;
                    }
                }
                catch { }
                return "system";
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                        k.SetValue("Theme", value == "light" || value == "dark" ? value : "system", RegistryValueKind.String);
                }
                catch { }
            }
        }

        public static Theme Load(bool? force)
        {
            var t = new Theme();
            string mode = Mode;
            t.Dark = force.HasValue ? force.Value : mode == "dark" ? true : mode == "light" ? false : ReadDark();
            t.Accent = ReadAccent(t.Dark);
            t.Build();
            return t;
        }

        static bool ReadDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return true;
        }

        static Color ReadAccent(bool dark)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    int i = dark ? 1 : 4;
                    if (p != null && p.Length >= 32) return Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
                }
            }
            catch { }
            return dark ? Color.FromRgb(96, 205, 255) : Color.FromRgb(0, 95, 184);
        }

        static Color Rgb(int r, int g, int b) { return Color.FromRgb((byte)r, (byte)g, (byte)b); }
        static Color Gray(int v) { return Rgb(v, v, v); }

        static Color Blend(Color baseColor, Color top, double amount)
        {
            return Color.FromRgb(
                (byte)(baseColor.R + (top.R - baseColor.R) * amount),
                (byte)(baseColor.G + (top.G - baseColor.G) * amount),
                (byte)(baseColor.B + (top.B - baseColor.B) * amount));
        }

        void Set(string name, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            brushes[name] = b;
        }

        void Build()
        {
            bool d = Dark;
            Color card = d ? Gray(43) : Gray(255);
            Set("Bg", d ? Gray(32) : Gray(243));
            Set("Card", card);
            Set("Divider", d ? Gray(30) : Gray(234));
            Set("Text", d ? Gray(255) : Gray(27));
            Set("SubText", d ? Gray(160) : Gray(96));
            Set("Hover", d ? Gray(52) : Gray(245));
            Set("Footer", d ? Gray(28) : Gray(238));
            Set("Stroke", d ? Gray(60) : Gray(218));
            Set("Scroll", d ? Gray(96) : Gray(180));
            Set("ToggleOff", d ? Gray(205) : Gray(135));
            Set("KnobOn", d ? Gray(0) : Gray(255));
            Set("Accent", Accent);
            Set("AccentText", d ? Gray(0) : Gray(255));

            double soft = d ? 0.22 : 0.14;
            Color good = d ? Rgb(108, 203, 95) : Rgb(15, 123, 15);
            Color warn = d ? Rgb(252, 185, 0) : Rgb(157, 93, 0);
            Color bad = d ? Rgb(255, 153, 164) : Rgb(196, 43, 28);
            Color neutral = d ? Gray(150) : Gray(120);
            Set("Good", good);
            Set("Warn", warn);
            Set("Bad", bad);
            Set("Neutral", neutral);
            Set("AccentSoft", Blend(card, Accent, soft));
            Set("GoodSoft", Blend(card, good, soft));
            Set("WarnSoft", Blend(card, warn, soft));
            Set("BadSoft", Blend(card, bad, soft));
            Set("NeutralSoft", Blend(card, neutral, soft));
        }

        public Brush this[string name] { get { return brushes[name]; } }

        public void Apply(ResourceDictionary rd)
        {
            foreach (var kv in brushes) rd[kv.Key] = kv.Value;
        }
    }

    // ---------------------------------------------------------------- Binding base class

    abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        public void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // ---------------------------------------------------------------- XAML resources

    static class Xaml
    {
        // Shared styles go into application resources so StaticResource references resolve while windows load
        static bool stylesLoaded;

        public static void LoadStyles(Application app)
        {
            if (stylesLoaded) return;
            stylesLoaded = true;
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Load("NetWatchdogUI.Styles.xaml"));
        }

        // The executing assembly is the XAML "local assembly", so xmlns:local="clr-namespace:NetWatchdogUI"
        // works without naming the assembly (the installer's file name changes with the version)
        public static object Load(string resourceName)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (s == null) throw new FileNotFoundException("Missing embedded resource " + resourceName);
                var settings = new XamlXmlReaderSettings { LocalAssembly = Assembly.GetExecutingAssembly() };
                using (var reader = new XamlXmlReader(s, System.Windows.Markup.XamlReader.GetWpfSchemaContext(), settings))
                    return System.Windows.Markup.XamlReader.Load(reader);
            }
        }
    }

    // ---------------------------------------------------------------- Win32

    // Locations shared by the panel, its elevated helpers and the installer
    static class AppPaths
    {
        public const string UserKey = @"Software\NetWatchdog";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static string DataDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetWatchdog"); } }

        // Readers (the SYSTEM watchdog among them) never see a half-written file
        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
            else System.IO.File.Move(tmp, path);
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        // Captures only the window itself, even when other windows cover it, so screenshots never show anything else
        public static void SaveWindow(IntPtr hwnd, string path)
        {
            RECT r;
            GetWindowRect(hwnd, out r);
            using (var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(hwnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }


        public static double ScaleAt(int x, int y)
        {
            try
            {
                var p = new POINT { X = x, Y = y };
                IntPtr mon = MonitorFromPoint(p, 2);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch { }
            return 1.0;
        }

        // Windows 11 rounded corners
        public static void RoundCorners(IntPtr hwnd)
        {
            int round = 2;
            try { DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch { }
        }

        public static void StyleFlyout(IntPtr hwnd)
        {
            RoundCorners(hwnd);
            // Tool window: hidden from Alt+Tab and the taskbar
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        }
    }

    // ---------------------------------------------------------------- Shortcuts

    // Creates .lnk files through IShellLink, without depending on Windows Script Host
    static class Shortcut
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, int flags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        public static void Create(string lnkPath, string target, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target));
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
            Marshal.ReleaseComObject(link);
        }
    }
}
