// Code shared by the tray panel and the installer: UI language, system theme and colors,
// data-binding base class, XAML resource loading, Win32 interop and shortcuts.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
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

    // English or Simplified Chinese. Follows the Windows display language unless --lang en|zh is given.
    // Strings are written as pairs at the call site: L.T("English", "中文").
    public static class L
    {
        public static bool Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

        public static void Init(string forced)
        {
            if (forced == "zh") Zh = true;
            else if (forced == "en") Zh = false;
        }

        public static string T(string en, string zh) { return Zh ? zh : en; }

        public static string F(string en, string zh, params object[] args) { return string.Format(T(en, zh), args); }
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

    // Follows the system (taskbar) light/dark mode and accent color; colors match Windows 11 Quick Settings
    class Theme
    {
        public bool Dark;
        public Color Accent;
        readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();

        public static Theme Load(bool? force)
        {
            var t = new Theme();
            t.Dark = force.HasValue ? force.Value : ReadDark();
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
        public static void LoadStyles(Application app)
        {
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

    static class Native
    {
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

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
