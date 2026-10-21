// NetWatchdog tray panel.
// Interaction follows TouchToggle: left-click the tray icon to open a Quick Settings style flyout,
// click elsewhere or press Esc to close it; the right-click menu offers Open panel, Open log and Exit.
// The panel shows the status.json written by the watchdog. User actions are written as request files
// into the queue directory and carried out by the watchdog, which runs as SYSTEM, so no UAC prompt appears.
// Compiled with the csc.exe that ships with .NET Framework (C# 5); see build.ps1.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("NetWatchdog tray panel")]
[assembly: AssemblyDescription("NetWatchdog tray panel")]

namespace NetWatchdogUI
{
    // ---------------------------------------------------------------- Startup

    class Options
    {
        public bool Tray;
        public string Snapshot;
        public bool? ForceDark;
        public string StatusPath;
        public string UpdateUrl, Page;
        public bool CheckUpdate;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            o.StatusPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetWatchdog", "status.json");
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--tray") o.Tray = true;
                else if (a == "--dark") o.ForceDark = true;
                else if (a == "--light") o.ForceDark = false;
                else if (a == "--snapshot" && i + 1 < args.Length) o.Snapshot = args[++i];
                else if (a == "--status" && i + 1 < args.Length) o.StatusPath = args[++i];
                else if (a == "--lang" && i + 1 < args.Length) L.Init(args[++i].ToLowerInvariant());
                else if (a == "--update-url" && i + 1 < args.Length) o.UpdateUrl = args[++i];
                else if (a == "--check-update") o.CheckUpdate = true;
                else if (a == "--page" && i + 1 < args.Length) o.Page = args[++i].ToLowerInvariant();
            }
            return o;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Elevated helper: started through UAC by the panel to approve a system-changing repair
            if (args.Length == 2 && args[0] == "--allow-repair") return AllowRepair(args[1]);
            var opt = Options.Parse(args);
            Mutex mutex = null;
            EventWaitHandle showEvent = null;
            if (opt.Snapshot == null)
            {
                bool created;
                mutex = new Mutex(true, @"Local\NetWatchdogUI", out created);
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NetWatchdogUI.Show");
                if (!created)
                {
                    // Already running: ask that instance to open its flyout
                    if (!opt.Tray) showEvent.Set();
                    return 0;
                }
            }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += (s, e) => { LogError(e.Exception); e.Handled = true; };
            var ctl = new Controller(opt);
            app.Startup += (s, e) => ctl.Start(showEvent);
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        public static readonly string[] AdminRepairs = { "proxy", "dhcp", "hosts", "tcpip", "reboot", "netreset" };

        // Adds the repair to admin-allow.json, which only administrators can write; the watchdog runs
        // system-changing repairs only when they are listed there
        static int AllowRepair(string id)
        {
            if (Array.IndexOf(AdminRepairs, id) < 0) return 2;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetWatchdog", "admin-allow.json");
                var ser = new JavaScriptSerializer();
                var ids = new List<string>();
                if (File.Exists(path))
                {
                    var d = ser.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                    object list;
                    if (d != null && d.TryGetValue("repairs", out list) && list is object[]) ids.AddRange(((object[])list).Select(x => Convert.ToString(x)));
                }
                if (!ids.Contains(id)) ids.Add(id);
                File.WriteAllText(path, ser.Serialize(new Dictionary<string, object> { { "repairs", ids.ToArray() } }), new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex) { LogError(ex); return 1; }
        }

        public static void LogError(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetWatchdog");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "ui-error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- Watchdog status

    class LineStatus
    {
        public string Alias, Type, AdapterStatus, Ip;
        public bool Healthy, Enabled;
        public int? Latency;
        public DateTime Since;
        public int[] History;
    }

    // Probe and Wi-Fi settings currently in effect (config.json with panel overrides applied)
    class SettingsInfo
    {
        public string[] Targets = new string[0];
        public int FailTargets, Timeout, Interval, FailThreshold, Recover, WifiRetry;
        public bool WifiHunt;
        public string Language = "auto";
        public string[] Overridden = new string[0];
        public List<RepairInfo> Repairs = new List<RepairInfo>();
        public string[] AdminAllowed = new string[0];
        public int RebootMaxPerDay = 3;

        public SettingsInfo Clone()
        {
            var c = (SettingsInfo)MemberwiseClone();
            c.Repairs = Repairs.Select(r => r.Clone()).ToList();
            return c;
        }

        // The form sent to and compared with the watchdog: id, enabled, after, cdOffline, cdOnline
        public static object[] Wire(List<RepairInfo> list)
        {
            return list.Select(r => (object)new Dictionary<string, object> {
                { "id", r.Id }, { "enabled", r.Enabled }, { "after", r.After }, { "cdOffline", r.CdOffline }, { "cdOnline", r.CdOnline } }).ToArray();
        }

        public object Get(string key)
        {
            switch (key)
            {
                case "targets": return Targets;
                case "failTargets": return FailTargets;
                case "pingTimeoutMs": return Timeout;
                case "intervalSeconds": return Interval;
                case "failThreshold": return FailThreshold;
                case "recoverThreshold": return Recover;
                case "wifiRetryMinutes": return WifiRetry;
                case "wifiHunt": return WifiHunt;
                case "language": return Language;
                case "repairActions": return Wire(Repairs);
                case "rebootMaxPerDay": return RebootMaxPerDay;
            }
            return null;
        }

        public void Set(string key, object v)
        {
            switch (key)
            {
                case "targets": Targets = (string[])v; break;
                case "failTargets": FailTargets = (int)v; break;
                case "pingTimeoutMs": Timeout = (int)v; break;
                case "intervalSeconds": Interval = (int)v; break;
                case "failThreshold": FailThreshold = (int)v; break;
                case "recoverThreshold": Recover = (int)v; break;
                case "wifiRetryMinutes": WifiRetry = (int)v; break;
                case "wifiHunt": WifiHunt = (bool)v; break;
                case "language": Language = (string)v; break;
                case "repairActions":
                    Repairs = ((object[])v).OfType<Dictionary<string, object>>().Select(d =>
                    {
                        var old = Repairs.FirstOrDefault(r => r.Id == (string)d["id"]);
                        return new RepairInfo {
                            Id = (string)d["id"], Enabled = (bool)d["enabled"], After = (int)d["after"],
                            CdOffline = (int)d["cdOffline"], CdOnline = (int)d["cdOnline"],
                            Admin = old != null && old.Admin, LastRun = old == null ? null : old.LastRun };
                    }).ToList();
                    break;
                case "rebootMaxPerDay": RebootMaxPerDay = (int)v; break;
            }
        }
    }

    class RepairInfo
    {
        public string Id;
        public bool Enabled, Admin;
        public int After, CdOffline = 86400, CdOnline = 18000;
        public DateTime? LastRun;
        public RepairInfo Clone() { return (RepairInfo)MemberwiseClone(); }
    }

    class WatchdogStatus
    {
        public DateTime Updated, Started, ActiveSince;
        public bool DryRun, Paused;
        public string Active, RouteOwner, LogPath, ConfigPath;
        public string AttemptAlias, AttemptSsid;
        public int AttemptIndex, AttemptTotal;
        public int Interval;
        public string[] Targets;
        public List<LineStatus> Lines = new List<LineStatus>();
        public SettingsInfo Settings;
        public DateTime? OutageSince, RebootAt;
        public string[] RepairsDone = new string[0];
        public int WifiBackupCount;
        public DateTime WifiBackupCreated;

        public static WatchdogStatus Load(string path)
        {
            string text;
            // The watchdog replaces the whole file on every update; allow that while reading
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = sr.ReadToEnd();

            var d = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (d == null) throw new InvalidDataException("status.json is malformed");
            var s = new WatchdogStatus();
            s.Updated = Date(d, "updated");
            s.Started = Date(d, "started");
            s.ActiveSince = Date(d, "activeSince");
            s.DryRun = Bool(d, "dryRun");
            s.Paused = Bool(d, "paused");
            s.Active = Str(d, "active");
            s.RouteOwner = Str(d, "routeOwner");
            object att;
            var a = d.TryGetValue("wifiAttempt", out att) ? att as Dictionary<string, object> : null;
            if (a != null)
            {
                s.AttemptAlias = Str(a, "alias");
                s.AttemptSsid = Str(a, "ssid");
                s.AttemptIndex = Int(a, "index") ?? 0;
                s.AttemptTotal = Int(a, "total") ?? 0;
            }
            object so;
            var sd = d.TryGetValue("settings", out so) ? so as Dictionary<string, object> : null;
            if (sd != null)
            {
                var c = new SettingsInfo();
                c.Targets = Arr(sd, "targets").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
                c.FailTargets = Int(sd, "failTargets") ?? 1;
                c.Timeout = Int(sd, "pingTimeoutMs") ?? 1000;
                c.Interval = Int(sd, "intervalSeconds") ?? 5;
                c.FailThreshold = Int(sd, "failThreshold") ?? 3;
                c.Recover = Int(sd, "recoverThreshold") ?? 3;
                c.WifiRetry = Int(sd, "wifiRetryMinutes") ?? 5;
                c.WifiHunt = !sd.ContainsKey("wifiHunt") || Bool(sd, "wifiHunt");
                c.Language = Str(sd, "language") ?? "auto";
                c.Overridden = Arr(sd, "overridden").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
                c.AdminAllowed = Arr(sd, "adminAllowed").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
                c.RebootMaxPerDay = Int(sd, "rebootMaxPerDay") ?? 3;
                foreach (var ro in Arr(sd, "repairActions").OfType<Dictionary<string, object>>())
                {
                    var last = Date(ro, "lastRun");
                    c.Repairs.Add(new RepairInfo {
                        Id = Str(ro, "id"), Enabled = Bool(ro, "enabled"), After = Int(ro, "after") ?? 60, Admin = Bool(ro, "admin"),
                        CdOffline = Int(ro, "cdOffline") ?? 86400, CdOnline = Int(ro, "cdOnline") ?? 18000,
                        LastRun = last == DateTime.MinValue ? (DateTime?)null : last });
                }
                s.Settings = c;
            }
            var os = Date(d, "outageSince");
            if (os != DateTime.MinValue) s.OutageSince = os;
            var ra = Date(d, "rebootAt");
            if (ra != DateTime.MinValue) s.RebootAt = ra;
            s.RepairsDone = Arr(d, "repairsDone").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
            object wb;
            var wd = d.TryGetValue("wifiBackup", out wb) ? wb as Dictionary<string, object> : null;
            if (wd != null)
            {
                s.WifiBackupCount = Int(wd, "count") ?? 0;
                s.WifiBackupCreated = Date(wd, "created");
            }
            s.LogPath = Str(d, "logPath");
            s.ConfigPath = Str(d, "configPath");
            s.Interval = Int(d, "intervalSeconds") ?? 5;
            s.Targets = Arr(d, "targets").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
            foreach (var o in Arr(d, "interfaces"))
            {
                var x = o as Dictionary<string, object>;
                if (x == null) continue;
                var l = new LineStatus();
                l.Alias = Str(x, "alias");
                l.Type = Str(x, "type");
                l.AdapterStatus = Str(x, "adapterStatus");
                l.Ip = Str(x, "ip");
                l.Healthy = Bool(x, "healthy");
                l.Enabled = !x.ContainsKey("enabled") || Bool(x, "enabled");
                l.Latency = Int(x, "latencyMs");
                l.Since = Date(x, "since");
                l.History = Arr(x, "history").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray();
                s.Lines.Add(l);
            }
            return s;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        static int? Int(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return null;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        static DateTime Date(Dictionary<string, object> d, string k)
        {
            string s = Str(d, k);
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t))
                return t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
            return DateTime.MinValue;
        }

        static object[] Arr(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v))
            {
                var a = v as object[];
                if (a != null) return a;
                var l = v as System.Collections.ArrayList;
                if (l != null) return l.ToArray();
            }
            return new object[0];
        }
    }

    // ---------------------------------------------------------------- View models

    class LineVM : Bindable
    {
        string name, detail, roleText, glyph, chartTip;
        Brush roleBrush, iconBg, iconFg;
        int[] history;
        bool enabled;
        double toggleOpacity = 1;
        Visibility dividerVisibility;

        public string Alias;
        public string Name { get { return name; } set { Set(ref name, value); } }
        public string Detail { get { return detail; } set { Set(ref detail, value); } }
        public string RoleText { get { return roleText; } set { Set(ref roleText, value); } }
        public string Glyph { get { return glyph; } set { Set(ref glyph, value); } }
        public string ChartTip { get { return chartTip; } set { Set(ref chartTip, value); } }
        public Brush RoleBrush { get { return roleBrush; } set { Set(ref roleBrush, value); } }
        public Brush IconBg { get { return iconBg; } set { Set(ref iconBg, value); } }
        public Brush IconFg { get { return iconFg; } set { Set(ref iconFg, value); } }
        public int[] History { get { return history; } set { Set(ref history, value); } }
        public bool Enabled { get { return enabled; } set { Set(ref enabled, value); } }
        public double ToggleOpacity { get { return toggleOpacity; } set { Set(ref toggleOpacity, value); } }
        public Visibility DividerVisibility { get { return dividerVisibility; } set { Set(ref dividerVisibility, value); } }
    }

    class RepairVM : Bindable
    {
        string title, detail, badge, afterText, cooldownText, cdOfflineText, cdOnlineText;
        Brush badgeBrush;
        bool enabled;
        double toggleOpacity = 1;
        Visibility dividerVisibility, badgeVisibility, cooldownVisibility = Visibility.Collapsed;
        public string Id;
        public bool Expanded;
        public string CooldownText { get { return cooldownText; } set { Set(ref cooldownText, value); } }
        public string CdOfflineText { get { return cdOfflineText; } set { Set(ref cdOfflineText, value); } }
        public string CdOnlineText { get { return cdOnlineText; } set { Set(ref cdOnlineText, value); } }
        public Visibility CooldownVisibility { get { return cooldownVisibility; } set { Set(ref cooldownVisibility, value); } }
        public string Title { get { return title; } set { Set(ref title, value); } }
        public string Detail { get { return detail; } set { Set(ref detail, value); } }
        public string Badge { get { return badge; } set { Set(ref badge, value); } }
        public string AfterText { get { return afterText; } set { Set(ref afterText, value); } }
        public Brush BadgeBrush { get { return badgeBrush; } set { Set(ref badgeBrush, value); } }
        public bool Enabled { get { return enabled; } set { Set(ref enabled, value); } }
        public double ToggleOpacity { get { return toggleOpacity; } set { Set(ref toggleOpacity, value); } }
        public Visibility DividerVisibility { get { return dividerVisibility; } set { Set(ref dividerVisibility, value); } }
        public Visibility BadgeVisibility { get { return badgeVisibility; } set { Set(ref badgeVisibility, value); } }
    }

    class TargetVM
    {
        public string Text { get; set; }
        public string Glyph { get; set; }
    }

    class EventVM
    {
        public string Time { get; set; }
        public string Message { get; set; }
        public string Full { get; set; }
        public Brush Dot { get; set; }
    }

    class PanelVM : Bindable
    {
        string statusLine, bannerTitle, bannerDetail, bannerGlyph, bannerAction, footerText, linesHint;
        Brush heroBrush, bannerBg, bannerFg;
        Visibility bannerVisibility = Visibility.Collapsed, bannerActionVisibility = Visibility.Collapsed, eventsEmpty = Visibility.Visible;
        bool autoSwitch = true, autoStart;
        double autoSwitchOpacity = 1;

        Visibility mainV = Visibility.Visible, settingsV = Visibility.Collapsed;
        string settingsSubtitle, versionText, updateStatusText, newTarget = "", failTargetsText, failTargetsHint;
        string timeoutText, intervalText, failThresholdText, recoverText, wifiRetryText, repoText, resetHint;
        bool autoUpdate, wifiHunt = true, langAuto, langZh, langEn, canReset;

        public readonly ObservableCollection<LineVM> LinesList = new ObservableCollection<LineVM>();
        public readonly ObservableCollection<EventVM> EventsList = new ObservableCollection<EventVM>();
        public readonly ObservableCollection<TargetVM> TargetsList = new ObservableCollection<TargetVM>();
        public ObservableCollection<TargetVM> Targets { get { return TargetsList; } }
        public readonly ObservableCollection<RepairVM> RepairsList = new ObservableCollection<RepairVM>();
        public ObservableCollection<RepairVM> Repairs { get { return RepairsList; } }
        string rebootMaxText, wifiBackupText;
        Visibility wifiBackupVisibility = Visibility.Collapsed;
        public string RebootMaxText { get { return rebootMaxText; } set { Set(ref rebootMaxText, value); } }
        public string WifiBackupText { get { return wifiBackupText; } set { Set(ref wifiBackupText, value); } }
        public Visibility WifiBackupVisibility { get { return wifiBackupVisibility; } set { Set(ref wifiBackupVisibility, value); } }

        public Visibility MainVisibility { get { return mainV; } set { Set(ref mainV, value); } }
        public Visibility SettingsVisibility { get { return settingsV; } set { Set(ref settingsV, value); } }
        public string SettingsSubtitle { get { return settingsSubtitle; } set { Set(ref settingsSubtitle, value); } }
        public string VersionText { get { return versionText; } set { Set(ref versionText, value); } }
        public string UpdateStatusText { get { return updateStatusText; } set { Set(ref updateStatusText, value); } }
        public string NewTarget { get { return newTarget; } set { Set(ref newTarget, value); } }
        public string FailTargetsText { get { return failTargetsText; } set { Set(ref failTargetsText, value); } }
        public string FailTargetsHint { get { return failTargetsHint; } set { Set(ref failTargetsHint, value); } }
        public string TimeoutText { get { return timeoutText; } set { Set(ref timeoutText, value); } }
        public string IntervalText { get { return intervalText; } set { Set(ref intervalText, value); } }
        public string FailThresholdText { get { return failThresholdText; } set { Set(ref failThresholdText, value); } }
        public string RecoverText { get { return recoverText; } set { Set(ref recoverText, value); } }
        public string WifiRetryText { get { return wifiRetryText; } set { Set(ref wifiRetryText, value); } }
        public string RepoText { get { return repoText; } set { Set(ref repoText, value); } }
        public string ResetHint { get { return resetHint; } set { Set(ref resetHint, value); } }
        public bool AutoUpdate { get { return autoUpdate; } set { Set(ref autoUpdate, value); } }
        public bool WifiHunt { get { return wifiHunt; } set { Set(ref wifiHunt, value); } }
        public bool LangAuto { get { return langAuto; } set { Set(ref langAuto, value); } }
        public bool LangZh { get { return langZh; } set { Set(ref langZh, value); } }
        public bool LangEn { get { return langEn; } set { Set(ref langEn, value); } }
        public bool CanReset { get { return canReset; } set { Set(ref canReset, value); } }

        string tab = "general";
        public string Tab
        {
            get { return tab; }
            set
            {
                tab = value;
                foreach (var name in new[] { "TabGeneral", "TabProbe", "TabWifi", "TabRepair", "TabMore",
                                             "TabGeneralVisibility", "TabProbeVisibility", "TabWifiVisibility", "TabRepairVisibility", "TabMoreVisibility" })
                    Raise(name);
            }
        }
        public bool TabGeneral { get { return tab == "general"; } }
        public bool TabProbe { get { return tab == "probe"; } }
        public bool TabWifi { get { return tab == "wifi"; } }
        public bool TabMore { get { return tab == "more"; } }
        public bool TabRepair { get { return tab == "repair"; } }
        public Visibility TabRepairVisibility { get { return TabRepair ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility TabGeneralVisibility { get { return TabGeneral ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility TabProbeVisibility { get { return TabProbe ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility TabWifiVisibility { get { return TabWifi ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility TabMoreVisibility { get { return TabMore ? Visibility.Visible : Visibility.Collapsed; } }
        public ObservableCollection<LineVM> Lines { get { return LinesList; } }
        public ObservableCollection<EventVM> Events { get { return EventsList; } }

        public string StatusLine { get { return statusLine; } set { Set(ref statusLine, value); } }
        public Brush HeroBrush { get { return heroBrush; } set { Set(ref heroBrush, value); } }
        public string LinesHint { get { return linesHint; } set { Set(ref linesHint, value); } }
        public string BannerTitle { get { return bannerTitle; } set { Set(ref bannerTitle, value); } }
        public string BannerDetail { get { return bannerDetail; } set { Set(ref bannerDetail, value); } }
        public string BannerGlyph { get { return bannerGlyph; } set { Set(ref bannerGlyph, value); } }
        public string BannerAction { get { return bannerAction; } set { Set(ref bannerAction, value); } }
        public Brush BannerBg { get { return bannerBg; } set { Set(ref bannerBg, value); } }
        public Brush BannerFg { get { return bannerFg; } set { Set(ref bannerFg, value); } }
        public Visibility BannerVisibility { get { return bannerVisibility; } set { Set(ref bannerVisibility, value); } }
        public Visibility BannerActionVisibility { get { return bannerActionVisibility; } set { Set(ref bannerActionVisibility, value); } }
        public bool AutoSwitch { get { return autoSwitch; } set { Set(ref autoSwitch, value); } }
        public double AutoSwitchOpacity { get { return autoSwitchOpacity; } set { Set(ref autoSwitchOpacity, value); } }
        public bool AutoStart { get { return autoStart; } set { Set(ref autoStart, value); } }
        public Visibility EventsEmpty { get { return eventsEmpty; } set { Set(ref eventsEmpty, value); } }
        public string FooterText { get { return footerText; } set { Set(ref footerText, value); } }
    }

    // ---------------------------------------------------------------- Latency sparkline

    // Values follow the watchdog: >= 0 is latency in ms, -1 = has an address but no reply,
    // -2 = no link or address. The chart is right-aligned; failed rounds get a red tick at the bottom.
    public class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
            "Values", typeof(int[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
            "LineBrush", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FailBrushProperty = DependencyProperty.Register(
            "FailBrush", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
            "GridBrush", typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
            "Capacity", typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

        public int[] Values { get { return (int[])GetValue(ValuesProperty); } set { SetValue(ValuesProperty, value); } }
        public Brush LineBrush { get { return (Brush)GetValue(LineBrushProperty); } set { SetValue(LineBrushProperty, value); } }
        public Brush FailBrush { get { return (Brush)GetValue(FailBrushProperty); } set { SetValue(FailBrushProperty, value); } }
        public Brush GridBrush { get { return (Brush)GetValue(GridBrushProperty); } set { SetValue(GridBrushProperty, value); } }
        public int Capacity { get { return (int)GetValue(CapacityProperty); } set { SetValue(CapacityProperty, value); } }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var gridPen = new Pen(GridBrush, 1);
            gridPen.Freeze();
            dc.DrawLine(gridPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

            int[] all = Values;
            if (all == null || all.Length == 0) return;
            // With little data the chart spans at least 20 points; once full it scrolls with Capacity points
            int cap = Math.Max(2, Math.Min(Capacity, Math.Max(20, all.Length)));
            int[] v = all.Length > cap ? all.Skip(all.Length - cap).ToArray() : all;
            double step = w / (cap - 1);
            int offset = cap - v.Length;

            double max = 20;
            foreach (int x in v) if (x > max) max = x;
            max *= 1.3;
            double top = 3, bottom = h - 6;

            Color c = Colors.DodgerBlue;
            var scb = LineBrush as SolidColorBrush;
            if (scb != null) c = scb.Color;
            var fill = new LinearGradientBrush(Color.FromArgb(0x50, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
            fill.Freeze();
            var pen = new Pen(LineBrush, 1.4);
            pen.LineJoin = PenLineJoin.Round;
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            if (pen.CanFreeze) pen.Freeze();

            Func<int, double> X = i => (offset + i) * step;
            Func<int, double> Y = val => bottom - (val / max) * (bottom - top);

            int k = 0;
            while (k < v.Length)
            {
                if (v[k] < 0) { k++; continue; }
                int j = k;
                while (j + 1 < v.Length && v[j + 1] >= 0) j++;
                if (j == k)
                {
                    dc.DrawEllipse(LineBrush, null, new Point(X(k), Y(v[k])), 1.5, 1.5);
                }
                else
                {
                    var line = new StreamGeometry();
                    using (var ctx = line.Open())
                    {
                        ctx.BeginFigure(new Point(X(k), Y(v[k])), false, false);
                        for (int n = k + 1; n <= j; n++) ctx.LineTo(new Point(X(n), Y(v[n])), true, true);
                    }
                    line.Freeze();
                    var area = new StreamGeometry();
                    using (var ctx = area.Open())
                    {
                        ctx.BeginFigure(new Point(X(k), bottom), true, true);
                        for (int n = k; n <= j; n++) ctx.LineTo(new Point(X(n), Y(v[n])), true, true);
                        ctx.LineTo(new Point(X(j), bottom), true, true);
                    }
                    area.Freeze();
                    dc.DrawGeometry(fill, null, area);
                    dc.DrawGeometry(null, pen, line);
                }
                k = j + 1;
            }

            for (int n = 0; n < v.Length; n++)
            {
                if (v[n] == -1) dc.DrawRectangle(FailBrush, null, new Rect(X(n) - 1, h - 5, 2, 5));
                else if (v[n] == -2) dc.DrawRectangle(GridBrush, null, new Rect(X(n) - 1, h - 3, 2, 3));
            }
            if (v[v.Length - 1] >= 0) dc.DrawEllipse(LineBrush, null, new Point(X(v.Length - 1), Y(v[v.Length - 1])), 2.5, 2.5);
        }
    }

    // ---------------------------------------------------------------- Tray icon

    enum Kind { Normal, Backup, Down, Paused, Stale, Missing }

    static class TrayIcons
    {
        // Green with a check: primary line; amber with arrows: backup line; red with a cross: nothing works;
        // gray: paused or the watchdog is not running
        public static SD.Icon Make(Kind kind)
        {
            SD.Color fill;
            switch (kind)
            {
                case Kind.Normal: fill = SD.Color.FromArgb(46, 160, 67); break;
                case Kind.Backup: fill = SD.Color.FromArgb(219, 140, 0); break;
                case Kind.Down: fill = SD.Color.FromArgb(207, 34, 46); break;
                default: fill = SD.Color.FromArgb(128, 128, 128); break;
            }
            using (var bmp = new SD.Bitmap(32, 32))
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                using (var b = new SD.SolidBrush(fill)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var p = new SD.Pen(SD.Color.White, 3.2f))
                {
                    p.StartCap = SD.Drawing2D.LineCap.Round;
                    p.EndCap = SD.Drawing2D.LineCap.Round;
                    p.LineJoin = SD.Drawing2D.LineJoin.Round;
                    switch (kind)
                    {
                        case Kind.Normal:
                            g.DrawLines(p, new[] { new SD.PointF(9, 16.5f), new SD.PointF(14, 21.5f), new SD.PointF(23, 11) });
                            break;
                        case Kind.Backup:
                            g.DrawLine(p, 9, 12, 23, 12);
                            g.DrawLines(p, new[] { new SD.PointF(19, 8), new SD.PointF(23, 12), new SD.PointF(19, 16) });
                            g.DrawLine(p, 23, 20, 9, 20);
                            g.DrawLines(p, new[] { new SD.PointF(13, 16), new SD.PointF(9, 20), new SD.PointF(13, 24) });
                            break;
                        case Kind.Down:
                            g.DrawLine(p, 11, 11, 21, 21);
                            g.DrawLine(p, 21, 11, 11, 21);
                            break;
                        case Kind.Paused:
                            g.DrawLine(p, 13, 10, 13, 22);
                            g.DrawLine(p, 19, 10, 19, 22);
                            break;
                        default:
                            g.DrawLine(p, 10, 16, 22, 16);
                            break;
                    }
                }
                IntPtr h = bmp.GetHicon();
                var icon = (SD.Icon)SD.Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }
    }

    // ---------------------------------------------------------------- Controller

    class Controller
    {
        const string TaskName = "NetWatchdog";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "NetWatchdogUI";

        class Pending { public bool Desired; public DateTime Deadline; }

        readonly Options opt;
        readonly PanelVM vm = new PanelVM();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        Theme theme;
        Window flyout;
        Grid body;
        TranslateTransform slide;
        WF.NotifyIcon tray;
        SD.Icon trayIcon;
        Kind trayKind = (Kind)(-1);
        DispatcherTimer timer;
        WatchdogStatus st;
        bool stale;
        Kind kind = Kind.Missing;
        string lastSwitchKey;
        DateTime hiddenAt = DateTime.MinValue;
        System.Drawing.Point anchor;
        int tick;
        string noticeTitle, noticeDetail, bannerKind;
        bool noticeError;
        DateTime noticeUntil;

        // Settings page
        class PendingSetting { public object Value; public DateTime Deadline; }
        readonly Dictionary<string, PendingSetting> pendingSettings = new Dictionary<string, PendingSetting>();
        SettingsInfo cur;
        bool onSettings;
        static readonly Regex TargetPattern = new Regex(@"^(https?://\S+|[A-Za-z0-9.:\[\]-]+)$", RegexOptions.IgnoreCase);
        // key -> min, max, step
        static readonly Dictionary<string, int[]> StepRules = new Dictionary<string, int[]>
        {
            { "failTargets", new[] { 1, 10, 1 } },
            { "pingTimeoutMs", new[] { 250, 10000, 250 } },
            { "intervalSeconds", new[] { 1, 300, 1 } },
            { "failThreshold", new[] { 1, 20, 1 } },
            { "recoverThreshold", new[] { 1, 20, 1 } },
            { "wifiRetryMinutes", new[] { 1, 240, 1 } },
            { "rebootMaxPerDay", new[] { 1, 10, 1 } },
        };

        // Updates
        ReleaseInfo update;
        bool checking, downloading;
        int dlPercent;
        DateTime nextCheck = DateTime.MaxValue;
        string notifiedTag, lastCheckText;
        string logPath;
        long logLength = -1;
        DateTime logWrite;

        public Controller(Options o) { opt = o; }

        static string ExeDir { get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } }

        public void Start(EventWaitHandle showEvent)
        {
            theme = Theme.Load(opt.ForceDark);
            flyout = LoadFlyout();
            theme.Apply(flyout.Resources);
            flyout.DataContext = vm;
            body = (Grid)flyout.FindName("Body");
            slide = (TranslateTransform)body.RenderTransform;

            flyout.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
            flyout.SourceInitialized += (s, e) => Native.StyleFlyout(new WindowInteropHelper(flyout).Handle);
            flyout.Deactivated += (s, e) => { if (opt.Snapshot == null) HideFlyout(); };
            flyout.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) HideFlyout();
                else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox) { AddTarget(); e.Handled = true; }
            };
            flyout.DpiChanged += (s, e) => flyout.Dispatcher.BeginInvoke(new Action(Fit), DispatcherPriority.Loaded);
            flyout.Closing += (s, e) => { e.Cancel = true; HideFlyout(); };

            if (opt.Snapshot == null) CreateTray();

            vm.AutoStart = ReadAutoStart();
            if (opt.UpdateUrl != null) Updater.ApiUrl = opt.UpdateUrl;
            lastCheckText = Updater.LastCheck > DateTime.MinValue
                ? L.F("Last checked {0:yyyy-MM-dd HH:mm}", "上次检查于 {0:yyyy-MM-dd HH:mm}", Updater.LastCheck)
                : L.T("Not checked yet", "尚未检查");
            DateTime due = Updater.LastCheck.AddHours(24), soon = DateTime.Now.AddSeconds(30);
            nextCheck = due < soon ? soon : due;
            if (opt.CheckUpdate) CheckUpdate(false);
            Refresh();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                tick++;
                if (flyout.IsVisible || tick % 3 == 0) Refresh();
                if (Updater.Enabled && !checking && opt.Snapshot == null && DateTime.Now >= nextCheck) CheckUpdate(false);
            };
            timer.Start();

            if (showEvent != null)
            {
                ThreadPool.RegisterWaitForSingleObject(showEvent,
                    (state, timedOut) => flyout.Dispatcher.BeginInvoke(new Action(() => ShowFlyout(WF.Cursor.Position))),
                    null, -1, false);
            }

            // --page is applied only once the main page is on screen, so the settings page freezes at its real height
            if (opt.Snapshot != null) StartSnapshot();
            else if (!opt.Tray) { ShowFlyout(WF.Cursor.Position); OpenRequestedPage(); }
        }

        Window LoadFlyout()
        {
            Xaml.LoadStyles(Application.Current);
            return (Window)Xaml.Load("NetWatchdogUI.Flyout.xaml");
        }

        // ------------------------------------------------ Tray

        void CreateTray()
        {
            tray = new WF.NotifyIcon();
            tray.Text = L.T("NetWatchdog", "网络看门狗");
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add(L.T("Open panel", "打开面板"), null, (s, e) => ShowFlyout(WF.Cursor.Position));
            menu.Items.Add(L.T("Open log", "打开日志"), null, (s, e) => OpenLog());
            menu.Items.Add(L.T("Check for updates", "检查更新"), null, (s, e) =>
            {
                ShowFlyout(WF.Cursor.Position);
                ShowSettings(true, false);
                CheckUpdate(true);
            });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(L.T("Exit", "退出"), null, (s, e) => Exit());
            tray.ContextMenuStrip = menu;
            tray.MouseUp += (s, e) =>
            {
                if (e.Button != WF.MouseButtons.Left) return;
                if (flyout.IsVisible) { HideFlyout(); return; }
                // Clicking the tray icon first deactivates (and hides) the flyout; don't reopen it on the same click
                if ((DateTime.Now - hiddenAt).TotalMilliseconds < 300) return;
                ShowFlyout(WF.Cursor.Position);
            };
            UpdateTrayIcon();
            tray.Visible = true;
        }

        void UpdateTrayIcon()
        {
            if (tray == null) return;
            if (kind != trayKind)
            {
                var old = trayIcon;
                trayIcon = TrayIcons.Make(kind);
                tray.Icon = trayIcon;
                trayKind = kind;
                if (old != null) old.Dispose();
            }
            string tip = L.T("NetWatchdog", "网络看门狗") + " · " + vm.StatusLine;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        void Notify(string title, string text)
        {
            if (tray != null) tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None);
        }

        // ------------------------------------------------ Showing and placing the flyout

        void ShowFlyout(System.Drawing.Point at)
        {
            // Like Quick Settings, re-read light/dark mode and the accent color every time the flyout opens
            theme = Theme.Load(opt.ForceDark);
            theme.Apply(flyout.Resources);
            vm.AutoStart = ReadAutoStart();
            Refresh();

            anchor = at;
            bool wasVisible = flyout.IsVisible;
            flyout.Show();
            flyout.UpdateLayout();
            Fit();
            flyout.Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(flyout).Handle);
            if (!wasVisible) Animate();
        }

        const double MaxFlyoutHeight = 640, MinSettingsHeight = 420;
        // While the settings page is open the window keeps one height (taken from the main page), so it
        // never moves when pages or tabs change; tab content scrolls inside it. 0 = size to content.
        double frozenHeight;

        // Measure the content, then set the window's size and position in a single call: docked to the
        // work-area edge on the taskbar's side, 12 pixels away from it, never taller than the work area.
        // Letting WPF grow the window by itself would extend it downward first (over the taskbar and off
        // screen) and only move it back afterwards.
        void Fit()
        {
            // Visibility, not IsVisible: IsVisible only turns true after the next layout pass
            var hwnd = new WindowInteropHelper(flyout).Handle;
            if (hwnd == IntPtr.Zero || flyout.Visibility != Visibility.Visible) return;
            var wa = WF.Screen.FromPoint(anchor).WorkingArea;
            double scale = Native.ScaleAt(anchor.X, anchor.Y);
            double natural;
            if (frozenHeight > 0) natural = frozenHeight;   // settings page: keep the size, do not float
            else
            {
                var root = (FrameworkElement)flyout.Content;
                root.Measure(new Size(flyout.Width, double.PositiveInfinity));
                natural = root.DesiredSize.Height;
            }
            double dip = Math.Min(natural, Math.Min(MaxFlyoutHeight, wa.Height / scale - 24));
            int w = (int)Math.Round(flyout.Width * scale);
            int h = (int)Math.Round(dip * scale);
            int m = (int)Math.Round(12 * scale);
            bool right = anchor.X > wa.Left + wa.Width / 2;
            bool bottom = anchor.Y > wa.Top + wa.Height / 2;
            int x = right ? wa.Right - w - m : wa.Left + m;
            int y = bottom ? wa.Bottom - h - m : wa.Top + m;
            double left = x / scale, top = y / scale;
            if (Math.Abs(flyout.Left - left) < 0.5 && Math.Abs(flyout.Top - top) < 0.5 && Math.Abs(flyout.Height - dip) < 0.5) return;
            // Go through WPF's own properties (it would undo a raw SetWindowPos on its next layout pass).
            // Order matters: when growing, move the top edge first; when shrinking, shrink first.
            // Either way the window never extends past the work area, not even for one frame.
            if (double.IsNaN(flyout.Height) || dip > flyout.Height)
            {
                flyout.Top = top;
                flyout.Left = left;
                flyout.Height = dip;
            }
            else
            {
                flyout.Height = dip;
                flyout.Top = top;
                flyout.Left = left;
            }
        }

        void Animate()
        {
            var wa = WF.Screen.FromPoint(anchor).WorkingArea;
            bool fromBelow = anchor.Y > wa.Top + wa.Height / 2;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(fromBelow ? 14 : -14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        void HideFlyout()
        {
            if (!flyout.IsVisible) return;
            flyout.Hide();
            hiddenAt = DateTime.Now;
            ShowSettings(false, false);
        }

        void Exit()
        {
            timer.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Current.Shutdown();
        }

        // ------------------------------------------------ Refresh

        void Refresh()
        {
            try
            {
                if (File.Exists(opt.StatusPath))
                {
                    try { st = WatchdogStatus.Load(opt.StatusPath); }
                    catch { /* caught the watchdog mid-replace; keep the previous status */ }
                }
                else st = null;

                stale = st != null && (DateTime.Now - st.Updated).TotalSeconds > Math.Max(90, st.Interval * 3 + 60);
                kind = ComputeKind();
                UpdatePanel();
                UpdateEvents();
                UpdateTrayIcon();
                MaybeNotify();
                if (flyout.IsVisible)
                {
                    flyout.UpdateLayout();
                    Fit();
                }
            }
            catch (Exception ex) { Program.LogError(ex); }
        }

        Kind ComputeKind()
        {
            if (st == null) return Kind.Missing;
            if (stale) return Kind.Stale;
            if (st.Paused) return Kind.Paused;
            if (st.Active == null) return Kind.Down;
            var first = st.Lines.FirstOrDefault(l => l.Enabled);
            return first != null && first.Alias == st.Active ? Kind.Normal : Kind.Backup;
        }

        LineStatus Line(string alias)
        {
            return st == null || alias == null ? null : st.Lines.FirstOrDefault(l => l.Alias == alias);
        }

        static string Ms(int? v) { return v.HasValue ? v.Value + " ms" : null; }

        static string Join(params string[] parts)
        {
            return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return L.F("{0} s", "{0} 秒", Math.Max(1, (int)t.TotalSeconds));
            if (t.TotalMinutes < 60) return L.F("{0} min", "{0} 分钟", (int)t.TotalMinutes);
            if (t.TotalHours < 24) return L.F("{0} h {1} min", "{0} 小时 {1} 分", (int)t.TotalHours, t.Minutes);
            return L.F("{0} d {1} h", "{0} 天 {1} 小时", (int)t.TotalDays, t.Hours);
        }

        string AttemptText()
        {
            return L.F("Trying \"{0}\" ({1}/{2})", "正在尝试「{0}」（{1}/{2}）", st.AttemptSsid, st.AttemptIndex, st.AttemptTotal);
        }

        void UpdatePanel()
        {
            var active = st == null ? null : Line(st.Active);
            switch (kind)
            {
                case Kind.Normal:
                    vm.StatusLine = Join(L.T("Online", "网络正常"), L.T("via ", "经由 ") + st.Active, Ms(active == null ? null : active.Latency));
                    vm.HeroBrush = theme["Good"];
                    break;
                case Kind.Backup:
                    vm.StatusLine = Join(L.T("On backup line ", "已切换到备用线路 ") + st.Active, Ms(active == null ? null : active.Latency));
                    vm.HeroBrush = theme["Warn"];
                    break;
                case Kind.Down:
                    vm.StatusLine = st.AttemptSsid != null
                        ? L.T("No line has internet · ", "可用的线路都不通 · ") + AttemptText()
                        : L.T("No line has internet, trying to recover", "可用的线路都不通，正在尝试恢复");
                    vm.HeroBrush = theme["Bad"];
                    break;
                case Kind.Paused:
                    vm.StatusLine = L.T("Switching paused · Windows is using ", "自动切换已暂停 · 系统正在使用 ") +
                        (st.RouteOwner ?? L.T("an unknown line", "未知线路"));
                    vm.HeroBrush = theme["Neutral"];
                    break;
                case Kind.Stale:
                    vm.StatusLine = L.T("The watchdog stopped responding", "看门狗已停止响应");
                    vm.HeroBrush = theme["Neutral"];
                    break;
                default:
                    vm.StatusLine = L.T("The watchdog is not running", "看门狗未运行");
                    vm.HeroBrush = theme["Neutral"];
                    break;
            }

            UpdateBanner();
            UpdateLines();

            // Automatic switching toggle
            bool auto = st == null || !st.Paused;
            Pending p;
            if (pending.TryGetValue("pause", out p))
            {
                if (auto == p.Desired) pending.Remove("pause");
                else if (DateTime.Now > p.Deadline)
                {
                    pending.Remove("pause");
                    SetError(L.T("The request had no effect", "请求没有生效"),
                             L.T("The watchdog did not respond to the switching request.", "看门狗没有响应切换自动模式的请求。"));
                }
                else auto = p.Desired;
            }
            vm.AutoSwitch = auto;
            vm.AutoSwitchOpacity = pending.ContainsKey("pause") ? 0.5 : 1;
            vm.Raise("AutoSwitch");
            vm.Raise("AutoStart");

            vm.LinesHint = st != null && st.Lines.Count > 1 ? L.T("In priority order", "按优先级排列") : "";
            UpdateSettings();
            if (st != null && !stale)
                vm.FooterText = Join(L.T("Running for ", "已运行 ") + Duration(DateTime.Now - st.Started),
                                     L.F("checks every {0} s", "每 {0} 秒检测", st.Interval));
            else
                vm.FooterText = L.T("Changes apply immediately", "更改立即生效");
        }

        void UpdateBanner()
        {
            string title = null, detail = null, action = null, glyph = "\uE7BA";
            Brush bg = theme["WarnSoft"], fg = theme["Warn"];

            bannerKind = null;
            if (noticeTitle != null && DateTime.Now < noticeUntil)
            {
                title = noticeTitle; detail = noticeDetail;
                if (noticeError) { bg = theme["BadSoft"]; fg = theme["Bad"]; glyph = "\uEA39"; }
                else { bg = theme["AccentSoft"]; fg = theme["Accent"]; glyph = "\uE946"; }
            }
            else if (st != null && !stale && st.RebootAt.HasValue && st.RebootAt.Value > DateTime.Now)
            {
                int left = (int)Math.Ceiling((st.RebootAt.Value - DateTime.Now).TotalSeconds);
                title = L.F("Restarting in {0} s", "{0} 秒后重启电脑", left);
                detail = L.T("NetWatchdog restarts the computer to finish repairing the network.", "网络看门狗将重启电脑以完成网络修复。");
                action = L.T("Cancel restart", "取消重启");
                bannerKind = "cancelreboot";
                bg = theme["BadSoft"]; fg = theme["Bad"]; glyph = "\uE7E8";
            }
            else if (st != null && !stale && st.WifiBackupCount > 0 && !st.OutageSince.HasValue)
            {
                title = st.WifiBackupCount == 1
                    ? L.T("1 Wi-Fi network can be restored", "有 1 个 Wi-Fi 配置可以恢复")
                    : L.F("{0} Wi-Fi networks can be restored", "有 {0} 个 Wi-Fi 配置可以恢复", st.WifiBackupCount);
                detail = L.F("Saved before the network reset on {0:yyyy-MM-dd HH:mm}. Restore imports them into Windows again.",
                             "这是 {0:yyyy-MM-dd HH:mm} 网络重置前备份的配置，恢复会把它们重新导入 Windows。", st.WifiBackupCreated);
                action = L.T("Restore", "恢复");
                bannerKind = "restorewifi";
                bg = theme["AccentSoft"]; fg = theme["Accent"]; glyph = "\uE701";
            }
            else if (kind == Kind.Missing)
            {
                bool canRepair = FindSetup() != null;
                title = L.T("The watchdog is not running", "看门狗没有运行");
                detail = canRepair
                    ? L.T("The panel reads its status from the background watchdog, and its status file is missing. Repair registers the background task again and needs administrator approval.",
                          "面板的状态来自后台看门狗，现在读不到它的状态文件。修复会重新注册后台任务，需要管理员确认。")
                    : L.T("The panel reads its status from the background watchdog, and its status file is missing. Install the watchdog with the setup package first.",
                          "面板的状态来自后台看门狗，现在读不到它的状态文件。请先运行安装包安装看门狗。");
                action = canRepair ? L.T("Repair", "修复") : L.T("Start", "启动");
                bannerKind = canRepair ? "repair" : "restart";
            }
            else if (kind == Kind.Stale)
            {
                title = L.T("The watchdog is not responding", "看门狗没有响应");
                detail = L.F("Its status stopped updating {0} ago.", "状态在 {0}前停止更新。", Duration(DateTime.Now - st.Updated));
                action = L.T("Restart", "重新启动");
                bannerKind = "restart";
            }
            else if (downloading)
            {
                title = L.F("Downloading version {0}…", "正在下载 {0} 版…", Ver(update));
                detail = L.F("{0}% · the installer starts once the download is verified", "{0}% · 下载并校验后会自动运行安装程序", dlPercent);
                bg = theme["AccentSoft"]; fg = theme["Accent"]; glyph = "\uE896";
            }
            else if (update != null)
            {
                bool verifiable = update.AssetUrl != null && update.Sha256 != null;
                title = L.F("Version {0} is available", "有新版本 {0}", Ver(update));
                detail = verifiable
                    ? L.F("You have {0}. Update downloads the installer, checks its SHA-256 and runs it.", "当前版本 {0}。更新会下载安装包，校验 SHA-256 后运行。", AppInfo.Version)
                    : L.F("You have {0}. This release has no verifiable installer; download it from the release page.", "当前版本 {0}。这个版本没有可校验的安装包，请到发布页面下载。", AppInfo.Version);
                action = verifiable ? L.T("Update", "更新") : L.T("View", "查看");
                bannerKind = verifiable ? "update" : "page";
                bg = theme["AccentSoft"]; fg = theme["Accent"]; glyph = "\uE896";
            }
            else if (st != null && st.DryRun)
            {
                title = L.T("Dry run", "演练模式");
                detail = L.T("The watchdog only logs its decisions and does not switch lines.", "看门狗只记录决策，不会真正切换线路。");
                bg = theme["AccentSoft"]; fg = theme["Accent"]; glyph = "\uE946";
            }

            vm.BannerVisibility = title == null ? Visibility.Collapsed : Visibility.Visible;
            vm.BannerTitle = title;
            vm.BannerDetail = detail;
            vm.BannerGlyph = glyph;
            vm.BannerBg = bg;
            vm.BannerFg = fg;
            vm.BannerAction = action;
            vm.BannerActionVisibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateLines()
        {
            var lines = st == null ? new List<LineStatus>() : st.Lines;
            if (vm.LinesList.Count != lines.Count || vm.LinesList.Where((x, i) => x.Alias != lines[i].Alias).Any())
            {
                vm.LinesList.Clear();
                foreach (var l in lines) vm.LinesList.Add(new LineVM { Alias = l.Alias });
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var x = vm.LinesList[i];
                bool wifi = l.Type == "wifi";
                x.Name = l.Alias;
                x.Detail = "  " + (wifi ? L.T("Wireless", "无线") : L.T("Wired", "有线"));
                x.Glyph = wifi ? "\uE701" : "\uE839";
                x.History = l.History;
                x.DividerVisibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;

                string role; Brush brush, soft;
                bool isActive = !stale && l.Alias == st.Active;
                bool isRoute = !stale && st.Paused && l.Alias == st.RouteOwner;
                bool trying = !stale && st.AttemptSsid != null && l.Alias == st.AttemptAlias;
                if (trying) { role = AttemptText(); brush = theme["Accent"]; soft = theme["AccentSoft"]; }
                else if (!l.Enabled) { role = L.T("Disabled · not used for switching", "已停用 · 不参与切换"); brush = theme["Neutral"]; soft = theme["NeutralSoft"]; }
                else if (isActive) { role = Join(L.T("Active line", "当前出口"), Ms(l.Latency)); brush = theme["Accent"]; soft = theme["AccentSoft"]; }
                else if (isRoute) { role = Join(L.T("Used by Windows", "系统正在使用"), Ms(l.Latency)); brush = theme["Accent"]; soft = theme["AccentSoft"]; }
                else if (l.Healthy) { role = Join(L.T("Backup · ready", "备用 · 可用"), Ms(l.Latency)); brush = theme["Good"]; soft = theme["GoodSoft"]; }
                else if (l.Ip != null) { role = L.T("No internet", "外网不通"); brush = theme["Bad"]; soft = theme["BadSoft"]; }
                else if (l.AdapterStatus == "Disabled") { role = L.T("Adapter disabled", "网卡已禁用"); brush = theme["Neutral"]; soft = theme["NeutralSoft"]; }
                else if (l.AdapterStatus == "Missing") { role = L.T("Adapter not found", "没有找到这块网卡"); brush = theme["Neutral"]; soft = theme["NeutralSoft"]; }
                else { role = L.T("Not connected", "未连接"); brush = theme["Neutral"]; soft = theme["NeutralSoft"]; }
                x.RoleText = role;
                x.RoleBrush = brush;
                x.IconBg = soft;
                x.IconFg = brush;

                int samples = l.History.Count(v => v >= -1);
                int lost = l.History.Count(v => v == -1);
                string addr = L.T("Address ", "地址 ") + (l.Ip ?? L.T("none", "无"));
                x.ChartTip = samples == 0
                    ? addr + L.T(" · no probe data yet", " · 最近没有可用的探测数据")
                    : addr + (samples == 1
                        ? L.F(" · last probe, {1:0}% lost", " · 最近 {0} 次探测，丢包 {1:0}%", samples, 100.0 * lost / samples)
                        : L.F(" · last {0} probes, {1:0}% lost", " · 最近 {0} 次探测，丢包 {1:0}%", samples, 100.0 * lost / samples));

                bool enabled = l.Enabled;
                Pending p;
                string key = "line:" + l.Alias;
                if (pending.TryGetValue(key, out p))
                {
                    if (enabled == p.Desired) pending.Remove(key);
                    else if (DateTime.Now > p.Deadline)
                    {
                        pending.Remove(key);
                        SetError(L.T("The request had no effect", "请求没有生效"),
                                 L.F("The watchdog did not respond to the request for {0}.", "看门狗没有响应对 {0} 的操作。", l.Alias));
                    }
                    else enabled = p.Desired;
                }
                x.Enabled = enabled;
                x.ToggleOpacity = pending.ContainsKey(key) ? 0.5 : 1;
                x.Raise("Enabled");
            }
        }

        // ------------------------------------------------ Recent events

        // Current log format: "yyyy-MM-dd HH:mm:ss [kind] message". Older lines without a kind are still shown.
        static readonly Regex LogLine = new Regex(@"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}):\d{2} (?:\[(\w+)\] )?(?:\[演练\] )?(.*)$");

        void UpdateEvents()
        {
            string path = st == null ? null : st.LogPath;
            if (path == null || !File.Exists(path))
            {
                if (vm.EventsList.Count > 0) vm.EventsList.Clear();
                vm.EventsEmpty = Visibility.Visible;
                logPath = null;
                return;
            }
            var fi = new FileInfo(path);
            if (path == logPath && fi.Length == logLength && fi.LastWriteTimeUtc == logWrite) return;
            logPath = path; logLength = fi.Length; logWrite = fi.LastWriteTimeUtc;

            var items = new List<EventVM>();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (string raw in ReadTail(path, 32 * 1024).AsEnumerable().Reverse())
            {
                var m = LogLine.Match(raw);
                if (!m.Success) continue;
                string tag = m.Groups[3].Value;
                string msg = m.Groups[4].Value;
                if (tag == "status" || msg.StartsWith("出口：")) continue;   // periodic summaries are not events
                string date = m.Groups[1].Value;
                items.Add(new EventVM
                {
                    Time = date == today ? m.Groups[2].Value : date.Substring(5) + " " + m.Groups[2].Value,
                    Message = msg,
                    Full = date + " " + m.Groups[2].Value + "  " + msg,
                    Dot = EventBrush(tag)
                });
                if (items.Count >= 4) break;
            }
            vm.EventsList.Clear();
            foreach (var e in items) vm.EventsList.Add(e);
            vm.EventsEmpty = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        Brush EventBrush(string tag)
        {
            switch (tag)
            {
                case "bad": return theme["Bad"];
                case "good": return theme["Good"];
                case "switch": return theme["Accent"];
                default: return theme["Neutral"];
            }
        }

        static List<string> ReadTail(string path, int bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - bytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int n = 0;
                while (n < buf.Length)
                {
                    int r = fs.Read(buf, n, buf.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
                var lines = new UTF8Encoding(false).GetString(buf, 0, n).Split('\n')
                    .Select(l => l.TrimEnd('\r').TrimStart('\uFEFF')).ToList();
                if (start > 0 && lines.Count > 0) lines.RemoveAt(0);   // the first line may be cut off
                return lines;
            }
        }

        // ------------------------------------------------ Switch notifications

        void MaybeNotify()
        {
            if (opt.Snapshot != null || st == null || stale) return;
            string key = kind + "|" + st.Active;
            if (lastSwitchKey != null && key != lastSwitchKey)
            {
                if (kind == Kind.Normal)
                    Notify(L.T("Back online", "网络已恢复"), L.F("Back on the primary line {0}", "已切回主线路 {0}", st.Active));
                else if (kind == Kind.Backup)
                    Notify(L.T("Switched to a backup line", "已切换到备用线路"), L.F("{0} is carrying the traffic", "{0} 正在承载流量", st.Active));
                else if (kind == Kind.Down)
                    Notify(L.T("No line has internet", "所有线路都不通"),
                           L.T("The watchdog keeps probing and trying backup lines", "看门狗会继续探测并尝试连接备用线路"));
            }
            lastSwitchKey = key;
        }

        // ------------------------------------------------ Actions

        void OnClick(object sender, RoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            string tag = fe == null ? null : fe.Tag as string;
            if (tag == null) return;
            switch (tag)
            {
                case "probe": SendRequest("probe"); break;
                case "settings": ShowSettings(true, true); break;
                case "back": ShowSettings(false, true); break;
                case "checkupdate": CheckUpdate(true); break;
                case "autoupdate":
                    Updater.Enabled = ((CheckBox)fe).IsChecked == true;
                    if (Updater.Enabled && nextCheck > DateTime.Now.AddHours(24)) nextCheck = DateTime.Now.AddSeconds(5);
                    Refresh();
                    break;
                case "wifihunt": ChangeSetting("wifiHunt", ((CheckBox)fe).IsChecked == true); break;
                case "tab:general":
                case "tab:probe":
                case "tab:wifi":
                case "tab:more":
                case "tab:repair":
                    ShowTab(tag.Substring(4));
                    break;
                case "repair": ToggleRepair(fe.DataContext as RepairVM, ((CheckBox)fe).IsChecked == true); break;
                case "rinc": StepRepair(fe.DataContext as RepairVM, 1); break;
                case "rdec": StepRepair(fe.DataContext as RepairVM, -1); break;
                case "repairnow": SendRequest("repair-now"); Refresh(); break;
                case "rexpand":
                {
                    var row = fe.DataContext as RepairVM;
                    if (row == null) break;
                    row.Expanded = !row.Expanded;
                    Refresh();
                    if (flyout.IsVisible) { flyout.UpdateLayout(); Fit(); }
                    break;
                }
                case "cdoffinc": StepCooldown(fe.DataContext as RepairVM, false, 1); break;
                case "cdoffdec": StepCooldown(fe.DataContext as RepairVM, false, -1); break;
                case "cdoninc": StepCooldown(fe.DataContext as RepairVM, true, 1); break;
                case "cdondec": StepCooldown(fe.DataContext as RepairVM, true, -1); break;
                case "restorewifi": RestoreWifi(); break;
                case "deletewifi":
                {
                    var answer = MessageBox.Show(
                        L.T("Delete the Wi-Fi backup? The saved networks in it, including their passwords, cannot be restored afterwards.",
                            "删除 Wi-Fi 备份？删除后，备份里的网络和密码都无法再恢复。"),
                        L.T("NetWatchdog", "网络看门狗"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (answer == MessageBoxResult.Yes) { SendRequest("delete-wifi-backup"); Refresh(); }
                    break;
                }
                case "repairlog":
                {
                    string path = Path.Combine(Path.GetDirectoryName(opt.StatusPath), "repair.log");
                    if (!File.Exists(path)) { SetNotice(L.T("No repair has run yet", "还没有执行过修复"), path, false); Refresh(); break; }
                    try { Process.Start("notepad.exe", "\"" + path + "\""); } catch (Exception ex) { Program.LogError(ex); }
                    break;
                }
                case "lang:auto": ChangeSetting("language", "auto"); break;
                case "lang:zh": ChangeSetting("language", "zh"); break;
                case "lang:en": ChangeSetting("language", "en"); break;
                case "addtarget": AddTarget(); break;
                case "removetarget": RemoveTarget(fe.DataContext as TargetVM); break;
                case "reset":
                    if (SendRequest("reset")) pendingSettings.Clear();
                    Refresh();
                    break;
                case "datafolder":
                    try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(opt.StatusPath) + "\""); } catch (Exception ex) { Program.LogError(ex); }
                    break;
                case "github":
                    try { Process.Start("https://github.com/" + AppInfo.Repo); } catch (Exception ex) { Program.LogError(ex); }
                    break;
                case "log": OpenLog(); break;
                case "exit": Exit(); break;
                case "banner": BannerAction(); break;
                case "autostart":
                    SetAutoStart(((CheckBox)fe).IsChecked == true);
                    vm.AutoStart = ReadAutoStart();
                    vm.Raise("AutoStart");
                    break;
                case "pause":
                {
                    bool on = ((CheckBox)fe).IsChecked == true;
                    if (SendRequest(on ? "resume" : "pause"))
                        pending["pause"] = new Pending { Desired = on, Deadline = DateTime.Now.AddSeconds(6) };
                    Refresh();
                    break;
                }
                default:
                    if (tag.StartsWith("inc:") || tag.StartsWith("dec:")) Step(tag.Substring(4), tag.StartsWith("inc:") ? 1 : -1);
                    break;
                case "line":
                {
                    var line = fe.DataContext as LineVM;
                    bool on = ((CheckBox)fe).IsChecked == true;
                    if (line == null) break;
                    if (!on && vm.LinesList.Count(l => l.Enabled) <= 1)
                    {
                        SetError(L.T("Keep at least one line", "至少要保留一条线路"),
                                 L.T("With every line disabled the watchdog has nothing to switch to.", "全部停用后看门狗就没有线路可以切换了。"));
                    }
                    else if (SendRequest((on ? "enable " : "disable ") + line.Alias))
                    {
                        pending["line:" + line.Alias] = new Pending { Desired = on, Deadline = DateTime.Now.AddSeconds(6) };
                    }
                    Refresh();
                    break;
                }
            }
        }

        void SetError(string title, string detail) { SetNotice(title, detail, true); }

        void SetNotice(string title, string detail, bool error)
        {
            noticeTitle = title;
            noticeDetail = detail;
            noticeError = error;
            noticeUntil = DateTime.Now.AddSeconds(8);
        }

        // Write a request file: write .tmp first and rename, so the watchdog only ever reads complete .req files
        bool SendRequest(string text)
        {
            if (st == null || stale)
            {
                SetError(L.T("The watchdog is not running", "看门狗没有运行"),
                         L.T("Start the watchdog before making changes.", "请先启动看门狗，再进行操作。"));
                Refresh();
                return false;
            }
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(opt.StatusPath), "queue");
                string name = DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string tmp = Path.Combine(dir, name + ".tmp");
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Move(tmp, Path.Combine(dir, name + ".req"));
                return true;
            }
            catch (Exception ex)
            {
                SetError(L.T("Cannot send the request", "无法发送请求"),
                         ex is UnauthorizedAccessException
                             ? L.T("This user cannot write to the request folder. Run the setup package again to grant access.",
                                   "当前用户没有写入请求目录的权限，请重新运行安装包。")
                             : ex.Message);
                Refresh();
                return false;
            }
        }

        void OpenLog()
        {
            string path = st != null && st.LogPath != null ? st.LogPath
                : Path.Combine(Path.GetDirectoryName(opt.StatusPath), "watchdog.log");
            if (!File.Exists(path)) { SetError(L.T("No log yet", "还没有日志"), path); Refresh(); return; }
            try { Process.Start("notepad.exe", "\"" + path + "\""); } catch (Exception ex) { Program.LogError(ex); }
        }

        // The install folder keeps a copy of the installer, used by "Installed apps" to uninstall and here to repair
        static string FindSetup()
        {
            string p = Path.Combine(ExeDir, "NetWatchdogSetup.exe");
            return File.Exists(p) ? p : null;
        }

        void BannerAction()
        {
            switch (bannerKind)
            {
                case "repair":
                    HideFlyout();
                    RunElevated(FindSetup(), "--repair", false);
                    break;
                case "restart":
                    HideFlyout();
                    RunElevated("cmd.exe", "/c schtasks /End /TN " + TaskName + " & schtasks /Run /TN " + TaskName, true);
                    break;
                case "update":
                    StartDownload();
                    break;
                case "cancelreboot":
                    SendRequest("cancel-reboot");
                    break;
                case "restorewifi":
                    RestoreWifi();
                    break;
                case "page":
                    HideFlyout();
                    try { Process.Start(update.Page); } catch (Exception ex) { Program.LogError(ex); }
                    break;
            }
        }

        // ------------------------------------------------ Settings page

        void ShowSettings(bool show, bool animate)
        {
            if (show == onSettings) return;
            onSettings = show;
            if (show)
            {
                // Freeze at the main page's natural height (measured while it is still the visible page)
                frozenHeight = 0;
                var root = (FrameworkElement)flyout.Content;
                root.Measure(new Size(flyout.Width, double.PositiveInfinity));
                frozenHeight = Math.Max(MinSettingsHeight, root.DesiredSize.Height);
            }
            else frozenHeight = 0;
            vm.MainVisibility = show ? Visibility.Collapsed : Visibility.Visible;
            vm.SettingsVisibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (flyout.IsVisible) { flyout.UpdateLayout(); Fit(); }
            if (!animate) return;
            var page = (FrameworkElement)flyout.FindName(show ? "SettingsPage" : "MainPage");
            var move = (TranslateTransform)page.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(show ? 40 : -40, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        void ShowTab(string name)
        {
            vm.Tab = name;
            ((ScrollViewer)flyout.FindName("SettingsScroll")).ScrollToTop();
            if (flyout.IsVisible) { flyout.UpdateLayout(); Fit(); }
        }

        // --page main | settings | settings:<general|probe|wifi|more>
        void OpenRequestedPage()
        {
            if (opt.Page == null || !opt.Page.StartsWith("settings")) return;
            ShowSettings(true, false);
            if (opt.Page.StartsWith("settings:")) ShowTab(opt.Page.Substring(9));
        }

        static string Rounds(int n) { return n == 1 ? L.T("1 round", "1 轮") : L.F("{0} rounds", "{0} 轮", n); }

        static string Ver(ReleaseInfo r) { return r == null ? "" : r.Tag.TrimStart('v', 'V'); }

        void UpdateSettings()
        {
            vm.VersionText = L.F("Version {0}", "版本 {0}", AppInfo.Version);
            vm.SettingsSubtitle = L.F("NetWatchdog {0}", "网络看门狗 {0}", AppInfo.Version);
            vm.UpdateStatusText = checking ? L.T("Checking for updates…", "正在检查更新…") : lastCheckText;
            vm.AutoUpdate = Updater.Enabled;
            vm.Raise("AutoUpdate");
            vm.RepoText = "github.com/" + AppInfo.Repo;

            var baseSettings = st != null && !stale ? st.Settings : null;
            if (baseSettings == null)
            {
                cur = null;
                vm.TargetsList.Clear();
                vm.FailTargetsText = vm.TimeoutText = vm.IntervalText = vm.FailThresholdText = vm.RecoverText = vm.WifiRetryText = "—";
                vm.FailTargetsHint = L.T("Available while the watchdog is running", "看门狗运行时才能修改");
                vm.ResetHint = vm.FailTargetsHint;
                vm.CanReset = false;
                return;
            }

            // Apply requests that the watchdog has not confirmed yet
            var ser = new JavaScriptSerializer();
            cur = baseSettings.Clone();
            foreach (var key in pendingSettings.Keys.ToList())
            {
                var p = pendingSettings[key];
                if (ser.Serialize(baseSettings.Get(key)) == ser.Serialize(p.Value)) pendingSettings.Remove(key);
                else if (DateTime.Now > p.Deadline)
                {
                    pendingSettings.Remove(key);
                    SetError(L.T("The setting was not applied", "设置没有生效"),
                             L.F("The watchdog did not accept the change to {0}.", "看门狗没有接受对 {0} 的修改。", key));
                }
                else cur.Set(key, p.Value);
            }

            if (vm.TargetsList.Count != cur.Targets.Length || vm.TargetsList.Where((t, i) => t.Text != cur.Targets[i]).Any())
            {
                vm.TargetsList.Clear();
                foreach (var t in cur.Targets)
                    vm.TargetsList.Add(new TargetVM { Text = t, Glyph = t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "\uE71B" : "\uE774" });
            }
            int n = Math.Max(1, cur.Targets.Length);
            int f = Math.Max(1, Math.Min(cur.FailTargets, n));
            vm.FailTargetsText = f + " / " + n;
            vm.FailTargetsHint = n == 1
                ? L.T("A round fails when the target fails", "目标失效，这一轮就算失败")
                : L.F("A round fails when {0} of the {1} targets fail", "{1} 个目标中有 {0} 个失效，这一轮就算失败", f, n);
            vm.TimeoutText = cur.Timeout + " ms";
            vm.IntervalText = L.F("{0} s", "{0} 秒", cur.Interval);
            vm.FailThresholdText = Rounds(cur.FailThreshold);
            vm.RecoverText = Rounds(cur.Recover);
            vm.WifiRetryText = L.F("{0} min", "{0} 分钟", cur.WifiRetry);
            vm.WifiHunt = cur.WifiHunt;
            vm.LangAuto = cur.Language == "auto";
            vm.LangZh = cur.Language == "zh";
            vm.LangEn = cur.Language == "en";
            foreach (var name in new[] { "WifiHunt", "LangAuto", "LangZh", "LangEn" }) vm.Raise(name);

            UpdateRepairs();

            int changed = baseSettings.Overridden.Length;
            vm.CanReset = changed > 0;
            vm.ResetHint = changed > 0
                ? L.F("{0} setting(s) were changed in this panel; reset returns to the config file", "有 {0} 项设置在面板上改过，恢复后使用配置文件中的值", changed)
                : L.T("All settings come from the config file", "所有设置都来自配置文件");
        }

        static readonly Dictionary<string, string[]> RepairText = new Dictionary<string, string[]>
        {
            // id -> English title, Chinese title, English detail, Chinese detail
            { "flushdns", new[] { "Flush DNS cache", "清除 DNS 缓存", "ipconfig /flushdns and /registerdns", "ipconfig /flushdns 与 /registerdns" } },
            { "arp", new[] { "Clear ARP and neighbor caches", "清除 ARP 与邻居缓存", "Also clears the destination cache", "同时清除目的地缓存" } },
            { "renew", new[] { "Renew the IP address", "重新获取 IP 地址", "Release and renew DHCP on the adapters that are down", "只对断网的网卡执行 ipconfig /release 与 /renew" } },
            { "netbios", new[] { "Reload NetBIOS names", "重载 NetBIOS 名称缓存", "nbtstat -R and -RR", "nbtstat -R 与 -RR" } },
            { "ssl", new[] { "Clear SSL state", "清除 SSL 状态", "Clears cached certificates of HTTPS sessions", "清除 HTTPS 会话缓存的证书状态" } },
            { "proxy", new[] { "Turn off the system proxy", "关闭系统代理", "Resets the WinHTTP proxy and turns off the proxy of signed-in users", "重置 WinHTTP 代理，并关闭已登录用户的系统代理" } },
            { "dhcp", new[] { "Switch back to DHCP", "恢复为 DHCP", "Removes static IP and DNS settings of the adapters that are down", "清除断网网卡上的静态 IP 和 DNS 设置" } },
            { "hosts", new[] { "Restore the default hosts file", "恢复默认 hosts", "The current file is backed up next to it first", "原文件会先备份在同一目录" } },
            { "tcpip", new[] { "Reset TCP/IP and Winsock", "重置 TCP/IP 与 Winsock", "Takes full effect after a restart", "重启后才完全生效" } },
            { "reboot", new[] { "Restart the computer", "重启电脑", "60 s countdown that can be cancelled here", "倒计时 60 秒，可在面板取消" } },
            { "netreset", new[] { "Windows network reset", "Windows 网络重置", "Removes and reinstalls every adapter, clears VPN and proxy settings, then restarts; saved Wi-Fi networks are backed up and can be restored here", "删除并重装所有网卡、清空 VPN 和代理设置后重启；已保存的 Wi-Fi 会先备份，之后可在这里手动恢复" } },
        };

        // Under 10 minutes in seconds (the stepper moves by 10 or 30 s there), above that in minutes or hours
        static string AfterText(int s)
        {
            if (s < 600 || s % 60 != 0) return L.F("{0} s", "{0} 秒", s);
            int m = s / 60;
            if (m % 60 == 0) return L.F("{0} h", "{0} 小时", m / 60);
            return L.F("{0} min", "{0} 分钟", m);
        }

        static int AfterStep(int s, int dir)
        {
            int step = s < 120 ? 10 : s < 600 ? 30 : s < 3600 ? 60 : 300;
            if (dir < 0 && s <= 120 && s > 110) step = 10;
            return Math.Max(10, Math.Min(86400, s + dir * step));
        }

        // Cooldowns: 0 means none; whole days, hours or minutes
        static string CdText(int s)
        {
            if (s == 0) return L.T("None", "不冷却");
            if (s % 60 != 0) return L.F("{0} s", "{0} 秒", s);
            if (s % 86400 == 0) return s == 86400 ? L.T("1 day", "1 天") : L.F("{0} days", "{0} 天", s / 86400);
            if (s % 3600 == 0) return L.F("{0} h", "{0} 小时", s / 3600);
            return L.F("{0} min", "{0} 分钟", s / 60);
        }

        // 10 min steps up to 1 h, 1 h steps up to 1 day, 6 h steps up to 7 days
        static int CdStep(int s, int dir)
        {
            int at = dir < 0 ? s - 1 : s;
            int step = at < 3600 ? 600 : at < 86400 ? 3600 : 21600;
            int next = s + dir * step;
            next -= next % step;
            if (dir < 0 && next < s - step) next = s - step;
            return Math.Max(0, Math.Min(604800, next));
        }

        void UpdateRepairs()
        {
            if (st != null && !stale && st.WifiBackupCount > 0)
            {
                vm.WifiBackupVisibility = Visibility.Visible;
                vm.WifiBackupText = L.F("{0} Wi-Fi network(s) backed up before the network reset on {1:yyyy-MM-dd HH:mm}",
                                        "{1:yyyy-MM-dd HH:mm} 网络重置前备份了 {0} 个 Wi-Fi 配置", st.WifiBackupCount, st.WifiBackupCreated);
            }
            else vm.WifiBackupVisibility = Visibility.Collapsed;
            if (cur == null) { vm.RepairsList.Clear(); vm.RebootMaxText = "—"; return; }
            if (vm.RepairsList.Count != cur.Repairs.Count || vm.RepairsList.Where((x, i) => x.Id != cur.Repairs[i].Id).Any())
            {
                vm.RepairsList.Clear();
                foreach (var r in cur.Repairs) vm.RepairsList.Add(new RepairVM { Id = r.Id });
            }
            bool pendingList = pendingSettings.ContainsKey("repairActions");
            for (int i = 0; i < cur.Repairs.Count; i++)
            {
                var r = cur.Repairs[i];
                var x = vm.RepairsList[i];
                string[] t;
                if (!RepairText.TryGetValue(r.Id, out t)) t = new[] { r.Id, r.Id, "", "" };
                x.Title = L.T(t[0], t[1]);
                x.Detail = L.T(t[2], t[3]);
                x.AfterText = AfterText(r.After);
                x.CdOfflineText = CdText(r.CdOffline);
                x.CdOnlineText = CdText(r.CdOnline);
                string cd = L.F("Cooldown {0} / {1}", "冷却 {0} / {1}", CdText(r.CdOffline), CdText(r.CdOnline));
                if (r.LastRun.HasValue) cd += L.F(" · last run {0:MM-dd HH:mm}", " · 上次 {0:MM-dd HH:mm}", r.LastRun.Value);
                x.CooldownText = cd + (x.Expanded ? "  \u25B4" : "  \u25BE");
                x.CooldownVisibility = x.Expanded ? Visibility.Visible : Visibility.Collapsed;
                x.Enabled = r.Enabled;
                x.Raise("Enabled");
                x.ToggleOpacity = pendingList ? 0.5 : 1;
                x.DividerVisibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
                bool admin = Array.IndexOf(Program.AdminRepairs, r.Id) >= 0;
                bool allowed = cur.AdminAllowed.Contains(r.Id);
                if (!admin) { x.BadgeVisibility = Visibility.Collapsed; continue; }
                x.BadgeVisibility = Visibility.Visible;
                if (r.Enabled && !allowed)
                {
                    x.Badge = L.T("Changes system settings · waiting for administrator approval", "会修改系统设置 · 等待管理员确认");
                    x.BadgeBrush = theme["Bad"];
                }
                else
                {
                    x.Badge = L.T("Changes system settings", "会修改系统设置");
                    x.BadgeBrush = theme["Warn"];
                }
            }
            vm.RebootMaxText = cur.RebootMaxPerDay == 1 ? L.T("1 time", "1 次") : L.F("{0} times", "{0} 次", cur.RebootMaxPerDay);
        }

        void ChangeRepair(string id, Action<RepairInfo> edit)
        {
            if (cur == null) { SendRequest("probe"); return; }
            var list = cur.Repairs.Select(r => r.Clone()).ToList();
            var item = list.FirstOrDefault(r => r.Id == id);
            if (item == null) return;
            edit(item);
            ChangeSetting("repairActions", SettingsInfo.Wire(list));
        }

        void ToggleRepair(RepairVM row, bool on)
        {
            if (row == null) return;
            // Turning a system-changing repair on needs an administrator once; turning it off never does
            if (on && Array.IndexOf(Program.AdminRepairs, row.Id) >= 0 && cur != null && !cur.AdminAllowed.Contains(row.Id))
                RunElevated(Assembly.GetExecutingAssembly().Location, "--allow-repair " + row.Id, true);
            ChangeRepair(row.Id, r => r.Enabled = on);
        }

        void StepRepair(RepairVM row, int dir)
        {
            if (row == null || cur == null) return;
            var r = cur.Repairs.FirstOrDefault(x => x.Id == row.Id);
            if (r == null) return;
            int next = AfterStep(r.After, dir);
            if (next != r.After) ChangeRepair(row.Id, x => x.After = next);
        }

        void StepCooldown(RepairVM row, bool online, int dir)
        {
            if (row == null || cur == null) return;
            var r = cur.Repairs.FirstOrDefault(x => x.Id == row.Id);
            if (r == null) return;
            int now = online ? r.CdOnline : r.CdOffline;
            int next = CdStep(now, dir);
            if (next == now) return;
            if (online) ChangeRepair(row.Id, x => x.CdOnline = next);
            else ChangeRepair(row.Id, x => x.CdOffline = next);
        }

        // Importing the backup is done by the watchdog, which runs as SYSTEM and can read the protected folder
        void RestoreWifi()
        {
            if (SendRequest("restore-wifi"))
                SetNotice(L.T("Restoring Wi-Fi networks…", "正在恢复 Wi-Fi 配置…"),
                          L.T("The result appears in the log.", "结果会写入日志。"), false);
            Refresh();
        }

        void ChangeSetting(string key, object value)
        {
            var ser = new JavaScriptSerializer();
            if (SendRequest("set " + key + " " + ser.Serialize(value)))
                pendingSettings[key] = new PendingSetting { Value = value, Deadline = DateTime.Now.AddSeconds(6) };
            Refresh();
        }

        void Step(string key, int direction)
        {
            int[] rule;
            if (cur == null || !StepRules.TryGetValue(key, out rule)) { SendRequest("probe"); return; }
            int max = key == "failTargets" ? Math.Max(1, cur.Targets.Length) : rule[1];
            int value = (int)cur.Get(key);
            if (key == "failTargets") value = Math.Max(1, Math.Min(value, max));
            int next = Math.Max(rule[0], Math.Min(max, value + direction * rule[2]));
            if (next != value) ChangeSetting(key, next);
        }

        void AddTarget()
        {
            if (cur == null) { SendRequest("probe"); return; }
            string t = (vm.NewTarget ?? "").Trim();
            if (t.Length == 0) return;
            var list = cur.Targets.ToList();
            if (t.Length > 200 || !TargetPattern.IsMatch(t))
                SetError(L.T("Invalid target", "目标格式不对"), L.T("Enter an IP address, a host name or an http(s):// URL.", "请填写 IP 地址、域名或 http(s):// 网址。"));
            else if (list.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                SetError(L.T("Already in the list", "已经在列表里了"), t);
            else if (list.Count >= 10)
                SetError(L.T("Too many targets", "目标太多"), L.T("Up to 10 probe targets are allowed.", "最多只能有 10 个探测目标。"));
            else
            {
                list.Add(t);
                ChangeSetting("targets", list.ToArray());
                vm.NewTarget = "";
                return;
            }
            Refresh();
        }

        void RemoveTarget(TargetVM item)
        {
            if (cur == null || item == null) return;
            var list = cur.Targets.Where(x => x != item.Text).ToList();
            if (list.Count == 0)
            {
                SetError(L.T("Keep at least one target", "至少要保留一个目标"), L.T("Without targets the watchdog cannot tell whether a line works.", "没有探测目标，看门狗就无法判断线路是否可用。"));
                Refresh();
                return;
            }
            ChangeSetting("targets", list.ToArray());
        }

        // ------------------------------------------------ Updates

        void CheckUpdate(bool manual)
        {
            if (checking) return;
            checking = true;
            Refresh();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ReleaseInfo r = null;
                Exception err = null;
                try { r = Updater.FetchLatest(); } catch (Exception ex) { err = ex; }
                flyout.Dispatcher.BeginInvoke(new Action(() =>
                {
                    checking = false;
                    nextCheck = DateTime.Now.AddHours(24);
                    if (err != null)
                    {
                        lastCheckText = L.F("Check failed: {0}", "检查失败：{0}", err.Message);
                        if (manual) SetError(L.T("Cannot check for updates", "无法检查更新"), err.Message);
                    }
                    else
                    {
                        Updater.LastCheck = DateTime.Now;
                        if (Updater.IsNewer(r))
                        {
                            update = r;
                            lastCheckText = L.F("Version {0} is available", "发现新版本 {0}", Ver(r));
                            if (notifiedTag != r.Tag && opt.Snapshot == null)
                                Notify(L.T("Update available", "有可用更新"), L.F("NetWatchdog {0} is available", "网络看门狗 {0} 已发布", Ver(r)));
                            notifiedTag = r.Tag;
                        }
                        else
                        {
                            update = null;
                            lastCheckText = L.F("Up to date · checked at {0:HH:mm}", "已是最新版本 · {0:HH:mm} 检查", DateTime.Now);
                            if (manual) SetNotice(L.T("You have the latest version", "已是最新版本"), L.F("Version {0}", "版本 {0}", AppInfo.Version), false);
                        }
                    }
                    Refresh();
                }));
            });
        }

        void StartDownload()
        {
            if (update == null || downloading) return;
            downloading = true;
            dlPercent = 0;
            Refresh();
            var r = update;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string path = null;
                Exception err = null;
                try
                {
                    path = Updater.Download(r, pct => flyout.Dispatcher.BeginInvoke(new Action(() => { dlPercent = pct; UpdateBanner(); })));
                }
                catch (Exception ex) { err = ex; }
                flyout.Dispatcher.BeginInvoke(new Action(() =>
                {
                    downloading = false;
                    if (err != null) SetError(L.T("Update failed", "更新失败"), err.Message);
                    else
                    {
                        // The installer asks for administrator approval itself, then upgrades in place
                        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); HideFlyout(); }
                        catch (Exception ex) { SetError(L.T("Cannot start the installer", "无法启动安装程序"), ex.Message); }
                    }
                    Refresh();
                }));
            });
        }

        void RunElevated(string file, string args, bool hidden)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { Verb = "runas", UseShellExecute = true };
                if (hidden) psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Win32Exception) { /* the user cancelled the UAC prompt */ }
        }

        static bool ReadAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex) { Program.LogError(ex); }
        }

        // ------------------------------------------------ Snapshots (for debugging and docs)

        // Render the whole content of a (scrollable) element to a PNG, on the theme background
        void RenderFull(FrameworkElement content, string path)
        {
            double w = content.ActualWidth, h = content.ActualHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(theme["Bg"], null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * 1.5), (int)(h * 1.5), 144, 144, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
        }

        // Open the flyout at the bottom-right of the primary screen, wait for the animation, capture exactly
        // the window (nothing around it, so other windows on the desktop never end up in the image), and exit.
        // On the settings page the whole page is also rendered to <name>-full.png.
        void StartSnapshot()
        {
            var wa = WF.Screen.PrimaryScreen.WorkingArea;
            ShowFlyout(new System.Drawing.Point(wa.Right - 20, wa.Bottom - 20));
            OpenRequestedPage();
            flyout.Topmost = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    Native.RECT r;
                    Native.GetWindowRect(new WindowInteropHelper(flyout).Handle, out r);
                    var rect = new SD.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                    using (var bmp = new SD.Bitmap(rect.Width, rect.Height))
                    {
                        using (var g = SD.Graphics.FromImage(bmp)) g.CopyFromScreen(rect.Location, SD.Point.Empty, rect.Size);
                        bmp.Save(opt.Snapshot, SD.Imaging.ImageFormat.Png);
                    }
                    if (onSettings)
                        RenderFull((FrameworkElement)((ScrollViewer)flyout.FindName("SettingsScroll")).Content,
                                   Path.ChangeExtension(opt.Snapshot, null) + "-full.png");
                }
                catch (Exception ex) { Program.LogError(ex); }
                Application.Current.Shutdown();
            };
            t.Start();
        }
    }
}
