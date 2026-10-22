// NetWatchdog installer.
// One exe installs, upgrades, repairs and uninstalls:
//   NetWatchdogSetup.exe                 install; upgrade or reinstall when already installed
//   NetWatchdogSetup.exe --repair        reinstall with the existing settings (the panel's Repair button)
//   NetWatchdogSetup.exe --uninstall     uninstall (the Uninstall button in Settings > Apps)
//   add --quiet to run without UI using default or existing settings; --lang en|zh|zh-TW|ja|ko picks the language
// Program files go to Program Files; config, log and state go to ProgramData\NetWatchdog.
// The watchdog script and the tray panel are embedded in this exe as resources.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("NetWatchdog Setup")]
[assembly: AssemblyDescription("NetWatchdog Setup")]

namespace NetWatchdogUI
{
    // ---------------------------------------------------------------- Command line

    class SetupArgs
    {
        public bool Uninstall, Repair, Quiet, Relocated, KeepPi;
        public bool? ForceDark;
        public string Snapshot, Page = "welcome", User, Dir;
        public List<string> Raw = new List<string>();

        public static SetupArgs Parse(string[] args)
        {
            var a = new SetupArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i].ToLowerInvariant();
                bool hasNext = i + 1 < args.Length;
                if (s == "--uninstall") a.Uninstall = true;
                else if (s == "--repair") a.Repair = true;
                else if (s == "--quiet") a.Quiet = true;
                else if (s == "--keep-pi") a.KeepPi = true;
                else if (s == "--relocated") { a.Relocated = true; continue; }
                else if (s == "--dark") a.ForceDark = true;
                else if (s == "--light") a.ForceDark = false;
                else if (s == "--user" && hasNext) { a.User = args[++i]; continue; }
                else if ((s == "--snapshot" || s == "--page" || s == "--dir" || s == "--lang") && hasNext)
                {
                    string v = args[++i];
                    if (s == "--snapshot") a.Snapshot = v;
                    else if (s == "--page") a.Page = v.ToLowerInvariant();
                    else if (s == "--dir") a.Dir = Path.Combine(v, "NetWatchdog");
                    else L.Init(v);
                    a.Raw.Add(args[i - 1]);
                    a.Raw.Add(v);
                    continue;
                }
                a.Raw.Add(args[i]);
            }
            return a;
        }

        public static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            // Double the run of backslashes before the closing quote, so a value ending in "\" stays quoted
            int tail = 0;
            while (tail < s.Length && s[s.Length - 1 - tail] == '\\') tail++;
            return "\"" + s.Replace("\"", "\\\"") + new string('\\', tail) + "\"";
        }

        public string CommandLine(params string[] extra)
        {
            return string.Join(" ", Raw.Concat(extra).Select(Quote));
        }
    }

    static class SetupProgram
    {
        public static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

        static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [STAThread]
        static int Main(string[] argv)
        {
            var a = SetupArgs.Parse(argv);

            // Start unelevated, remember the signed-in user, then relaunch ourselves elevated
            if (a.Snapshot == null && !IsAdmin())
            {
                string user = a.User ?? WindowsIdentity.GetCurrent().Name;
                try
                {
                    var psi = new ProcessStartInfo(SelfPath, a.CommandLine("--user", user)) { Verb = "runas", UseShellExecute = true };
                    var p = Process.Start(psi);
                    p.WaitForExit();
                    return p.ExitCode;
                }
                catch (Win32Exception) { return 1223; }   // the user cancelled the UAC prompt
            }
            if (a.User == null) a.User = WindowsIdentity.GetCurrent().Name;

            // The uninstaller lives in the install folder and a running exe cannot delete itself:
            // copy it to the temp folder and run the copy
            if (a.Uninstall && a.Snapshot == null && !a.Relocated)
            {
                // Run the copy from an admin-only folder, so another program cannot swap the elevated uninstaller
                string temp = Path.Combine(Installer.SecureSelfTempDir(), "NetWatchdog-uninstall.exe");
                File.Copy(SelfPath, temp, true);
                var p = Process.Start(new ProcessStartInfo(temp, a.CommandLine("--user", a.User, "--relocated")) { UseShellExecute = false });
                if (!a.Quiet) return 0;
                p.WaitForExit();
                return p.ExitCode;
            }

            int code;
            if (a.Quiet)
            {
                code = SetupController.RunQuiet(a);
            }
            else
            {
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (s, e) => { Installer.Log(e.Exception.ToString()); e.Handled = true; };
                Xaml.LoadStyles(app);
                var ctl = new SetupController(a);
                app.Startup += (s, e) => ctl.Start();
                app.Run();
                code = ctl.ExitCode;
            }
            if (a.Relocated) Installer.ScheduleSelfDelete();
            return code;
        }
    }

    // ---------------------------------------------------------------- Install logic

    class DetectedLine
    {
        public string Name, Type;
    }

    class InstalledInfo
    {
        public string Dir, Version, PanelSid;
        public bool Standby, Autostart, Shortcut;
    }

    class Plan
    {
        public string Dir, User;
        public bool Standby = true, Autostart = true, Shortcut = true, RemoveData, RemovePi = true;
        public List<DetectedLine> Lines = new List<DetectedLine>();
        // Results worked out while installing; written to the uninstall entry
        public bool StandbyByUs;
        public string PanelSid;
    }

    class Step
    {
        public string Name;
        public Action Run;
        public Step(string name, Action run) { Name = name; Run = run; }
    }

    static class Installer
    {
        public const string TaskName = "NetWatchdog";
        public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NetWatchdog";
        const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetWatchdog");
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "NetWatchdog-setup.log");
        public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetWatchdog"); } }
        public static string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }

        // The shortcut is named in the installer's language; uninstall removes every name
        static readonly string[] ShortcutNames = { "NetWatchdog.lnk", "网络看门狗.lnk", "網路看門狗.lnk" };
        static string ShortcutName { get { return L.Lang == "zh" ? ShortcutNames[1] : L.Lang == "zh-TW" ? ShortcutNames[2] : ShortcutNames[0]; } }
        static string ShortcutPath(string name) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name); }

        static readonly string[] ProgramFiles = { "NetWatchdog.ps1", "NetWatchdogUI.exe", "NetRepair.exe", "NetWatchdogSetup.exe" };

        public static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        // ------------------------------------------------ Queries

        public static InstalledInfo FindInstalled()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(UninstallKey))
            {
                if (k == null) return null;
                var i = new InstalledInfo();
                i.Dir = k.GetValue("InstallLocation") as string;
                i.Version = k.GetValue("DisplayVersion") as string;
                i.PanelSid = k.GetValue("NetWatchdogPanelSid") as string;
                i.Standby = Flag(k, "NetWatchdogStandby");
                i.Autostart = Flag(k, "NetWatchdogAutostart");
                i.Shortcut = Flag(k, "NetWatchdogShortcut");
                if (string.IsNullOrEmpty(i.Dir)) i.Dir = DefaultDir;
                return i;
            }
        }

        static bool Flag(RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            return v is int && (int)v != 0;
        }

        // Find one wired and one wireless adapter with Get-NetAdapter -Physical, preferring adapters that are up
        public static List<DetectedLine> DetectLines()
        {
            var result = new List<DetectedLine>();
            string script =
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                "ConvertTo-Json -Compress -InputObject @(Get-NetAdapter -Physical | Where-Object Status -ne 'Not Present' | " +
                "Select-Object Name, MediaType, @{n='Up';e={$_.Status -eq 'Up'}})";
            string output;
            if (RunPowerShell(script, out output) != 0) return result;
            object[] items;
            try { items = new JavaScriptSerializer().DeserializeObject(output.Trim()) as object[]; }
            catch { return result; }
            if (items == null) return result;

            var all = items.OfType<Dictionary<string, object>>().Select(d => new
            {
                Name = Convert.ToString(d["Name"]),
                Media = Convert.ToString(d["MediaType"]),
                Up = d["Up"] is bool && (bool)d["Up"]
            }).ToList();
            var wired = all.Where(x => x.Media == "802.3").OrderByDescending(x => x.Up).ThenBy(x => x.Name).FirstOrDefault();
            var wifi = all.Where(x => x.Media == "Native 802.11").OrderByDescending(x => x.Up).ThenBy(x => x.Name).FirstOrDefault();
            if (wired != null) result.Add(new DetectedLine { Name = wired.Name, Type = "wired" });
            if (wifi != null) result.Add(new DetectedLine { Name = wifi.Name, Type = "wifi" });
            return result;
        }

        // Lines from an existing config, shown when upgrading
        public static List<DetectedLine> ReadConfiguredLines()
        {
            var result = new List<DetectedLine>();
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath, Encoding.UTF8)) as Dictionary<string, object>;
                object list;
                if (d == null || !d.TryGetValue("interfaces", out list)) return null;
                foreach (var o in (object[])list)
                {
                    var x = o as Dictionary<string, object>;
                    if (x == null) continue;
                    object type;
                    result.Add(new DetectedLine
                    {
                        Name = Convert.ToString(x["alias"]),
                        Type = x.TryGetValue("type", out type) ? Convert.ToString(type) : "wired"
                    });
                }
                return result;
            }
            catch { return null; }
        }

        // ------------------------------------------------ Install

        public static List<Step> InstallSteps(Plan p, InstalledInfo old)
        {
            return new List<Step>
            {
                new Step(L.T("Stop the running version", "停止正在运行的旧版本"), () => StopRunning(false)),
                new Step(L.T("Copy program files", "复制程序文件"), () => CopyFiles(p)),
                new Step(L.T("Prepare config and data folder", "准备配置和数据目录"), () => PrepareData(p)),
                new Step(L.T("Register the background task", "注册后台任务"), () => RegisterTask(p)),
                new Step(L.T("Apply system settings", "应用系统设置"), () => ApplySettings(p, old)),
                new Step(L.T("Add to Installed apps", "登记到“已安装的应用”"), () => RegisterApp(p)),
            };
        }

        static void StopRunning(bool deleteTask)
        {
            Run("schtasks.exe", "/End /TN " + TaskName);
            if (deleteTask) Run("schtasks.exe", "/Delete /TN " + TaskName + " /F");
            StopAi();
            foreach (var proc in Process.GetProcessesByName("NetWatchdogUI"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch (Exception ex) { Log("Cannot end the tray panel: " + ex.Message); }
            }
            Thread.Sleep(500);
        }

        // A Pi process started by AI repair runs as SYSTEM and outlives the watchdog; it keeps ai\work open, so
        // it must be ended before the data folder can be removed
        static void StopAi()
        {
            try
            {
                string running = Path.Combine(DataDir, "ai", "running.json");
                if (!File.Exists(running)) return;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(running)) as Dictionary<string, object>;
                object pid;
                if (d != null && d.TryGetValue("pid", out pid))
                    Run("taskkill.exe", "/T /F /PID " + Convert.ToInt32(pid, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Log("Cannot end the AI process: " + ex.Message); }
        }

        // Only SYSTEM and administrators may write the install folder: the watchdog runs its script as SYSTEM,
        // so ordinary users must not be able to change it
        const string LockedAcl = "/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q";
        // Same, with no access for ordinary users: for folders that must never be read by them
        const string AdminOnlyAcl = "/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /Q";

        // A folder a normal user may have created before the install (ProgramData grants Users create rights) can
        // be a junction or still be owned by that user, who would keep control of it. Refuse a reparse point, then
        // take ownership and drop every inherited or user-granted ACE before applying our own.
        static void SecureExistingDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(L.F("{0} is a reparse point; delete it and run the installer again.", "{0} 是重解析点（junction/符号链接），请删除后重新运行安装程序。", dir));
            Run("icacls.exe", Q(dir) + " /setowner *S-1-5-32-544 /T /C /L /Q");
            Run("icacls.exe", Q(dir) + " /reset /T /C /L /Q");
        }

        // A temp file passed to an elevated tool, or run elevated, must sit where only administrators can write,
        // not in the user's %TEMP% where another program could swap it. Returns the folder (admin-only, fresh).
        // The relocated uninstaller: a fresh admin-only folder under %SystemRoot%\Temp, outside the data folder
        // it is about to delete (a running exe cannot be deleted); ScheduleSelfDelete removes it afterwards
        public static string SecureSelfTempDir()
        {
            string dir = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Temp", "NetWatchdog-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            Run("icacls.exe", Q(dir) + " " + AdminOnlyAcl);
            return dir;
        }

        static string SecureTempDir()
        {
            string dir = Path.Combine(DataDir, "setup-tmp");
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            Directory.CreateDirectory(dir);
            Run("icacls.exe", Q(dir) + " " + AdminOnlyAcl);
            return dir;
        }

        static void CopyFiles(Plan p)
        {
            SecureExistingDir(p.Dir);
            Directory.CreateDirectory(p.Dir);
            Check(Run("icacls.exe", Q(p.Dir) + " " + LockedAcl), L.T("Setting install folder permissions", "设置安装目录权限"));
            Extract("payload.NetWatchdog.ps1", Path.Combine(p.Dir, "NetWatchdog.ps1"));
            Extract("payload.NetWatchdogUI.exe", Path.Combine(p.Dir, "NetWatchdogUI.exe"));
            Extract("payload.NetRepair.exe", Path.Combine(p.Dir, "NetRepair.exe"));
            string dest = Path.Combine(p.Dir, "NetWatchdogSetup.exe");
            if (!string.Equals(Path.GetFullPath(SetupProgram.SelfPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                Retry(() => File.Copy(SetupProgram.SelfPath, dest, true));
        }

        static void Extract(string resource, string path)
        {
            Retry(() =>
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                using (var f = File.Create(path))
                {
                    if (s == null) throw new FileNotFoundException(L.F("The setup package is missing {0}", "安装包缺少 {0}", resource));
                    s.CopyTo(f);
                }
            });
            Log("Wrote " + path);
        }

        static void PrepareData(Plan p)
        {
            string queue = Path.Combine(DataDir, "queue");
            SecureExistingDir(DataDir);
            SecureExistingDir(queue);
            Directory.CreateDirectory(queue);
            Check(Run("icacls.exe", Q(DataDir) + " " + LockedAcl), L.T("Setting data folder permissions", "设置数据目录权限"));
            // The reset above made these inherit Users:RX again; they hold the API key and Wi-Fi passwords
            foreach (var sub in new[] { Path.Combine(DataDir, "ai", "agent"), Path.Combine(DataDir, "wifi-backup") })
                if (Directory.Exists(sub)) Check(Run("icacls.exe", Q(sub) + " " + AdminOnlyAcl), L.T("Setting data folder permissions", "设置数据目录权限"));
            // The request queue also grants Modify to the signed-in user, so the panel can send requests without UAC.
            // A deployment tool may run the installer as SYSTEM; then there is no single user, so Users get Modify.
            p.PanelSid = SidOf(p.User);
            Run("icacls.exe", Q(queue) + " /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /Q");
            string grantSid = (p.PanelSid == null || p.PanelSid == "S-1-5-18") ? "S-1-5-32-545" : p.PanelSid;
            Check(Run("icacls.exe", Q(queue) + " /grant:r *" + grantSid + ":(OI)(CI)M /Q"), L.T("Setting request folder permissions", "设置请求目录权限"));

            // Early PowerShell-script installs kept the config in the program folder
            string legacy = Path.Combine(p.Dir, "config.json");
            if (!File.Exists(ConfigPath) && File.Exists(legacy)) File.Move(legacy, ConfigPath);

            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath, BuildConfig(p.Lines), new UTF8Encoding(false));
                Log("Generated config " + ConfigPath);
            }
            else
            {
                string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var auto = new System.Text.RegularExpressions.Regex("\"language\"\\s*:\\s*\"auto\"");
                if (auto.IsMatch(text))
                {
                    File.WriteAllText(ConfigPath, auto.Replace(text, "\"language\": \"" + InstallLanguage + "\"", 1), new UTF8Encoding(false));
                    Log("Kept existing config " + ConfigPath + ", log language set to " + InstallLanguage);
                }
                else Log("Kept existing config " + ConfigPath);
            }
        }

        // The watchdog log uses the language chosen at install time, so it matches the panel and installer.
        // (With "auto" it would follow the system default language, which can differ from the user's.)
        static string InstallLanguage { get { return L.Lang; } }

        public static string BuildConfig(List<DetectedLine> lines)
        {
            var js = new JavaScriptSerializer();
            if (lines == null || lines.Count == 0)
                lines = new List<DetectedLine> { new DetectedLine { Name = "Ethernet", Type = "wired" }, new DetectedLine { Name = "Wi-Fi", Type = "wifi" } };
            var sb = new StringBuilder();
            sb.Append("{\n    \"interfaces\": [\n");
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                sb.Append("        { \"alias\": ").Append(js.Serialize(l.Name)).Append(", \"type\": \"").Append(l.Type).Append('"');
                if (l.Type == "wifi") sb.Append(", \"profiles\": []");
                sb.Append(" }").Append(i < lines.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("    ],\n");
            sb.Append("    \"targets\": [\"223.5.5.5\", \"119.29.29.29\", \"1.1.1.1\", \"8.8.8.8\"],\n");
            sb.Append("    \"minSuccess\": 1,\n");
            sb.Append("    \"intervalSeconds\": 5,\n");
            sb.Append("    \"pingTimeoutMs\": 1000,\n");
            sb.Append("    \"failThreshold\": 3,\n");
            sb.Append("    \"recoverThreshold\": 3,\n");
            sb.Append("    \"reconnectCooldownSeconds\": 30,\n");
            sb.Append("    \"wifiRetryMinutes\": 5,\n");
            sb.Append("    \"demotedMetric\": 9000,\n");
            sb.Append("    \"flushDnsOnSwitch\": true,\n");
            sb.Append("    \"statusEveryMinutes\": 60,\n");
            sb.Append("    \"historySize\": 120,\n");
            sb.Append("    \"language\": \"").Append(InstallLanguage).Append("\",\n");
            sb.Append("    \"logPath\": ").Append(js.Serialize(Path.Combine(DataDir, "watchdog.log"))).Append(",\n");
            sb.Append("    \"statusPath\": ").Append(js.Serialize(Path.Combine(DataDir, "status.json"))).Append(",\n");
            sb.Append("    \"logMaxKB\": 1024\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static void RegisterTask(Plan p)
        {
            string script = Path.Combine(p.Dir, "NetWatchdog.ps1");
            string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(script) + " -ConfigPath " + Q(ConfigPath);
            string description = L.T("Switches to a working line when the wired or Wi-Fi connection loses internet", "有线或 Wi-Fi 断网时自动切换到可用线路");
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Author>NetWatchdog</Author><Description>" + SecurityElement.Escape(description) + "</Description></RegistrationInfo>\n" +
                "  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                "  <Settings>\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
                "    <Enabled>true</Enabled>\n" +
                "    <Hidden>false</Hidden>\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
                "    <Priority>7</Priority>\n" +
                "    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>\n" +
                "  </Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>powershell.exe</Command><Arguments>" + SecurityElement.Escape(args) + "</Arguments></Exec></Actions>\n" +
                "</Task>\n";
            // The XML registers a task that runs as SYSTEM; write it where only administrators can swap it
            string dir = SecureTempDir();
            string tmp = Path.Combine(dir, "task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try { Check(Run("schtasks.exe", "/Create /TN " + TaskName + " /XML " + Q(tmp) + " /F"), L.T("Registering the scheduled task", "注册计划任务")); }
            finally { try { Directory.Delete(dir, true); } catch { } }
            Check(Run("schtasks.exe", "/Run /TN " + TaskName), L.T("Starting the scheduled task", "启动计划任务"));
        }

        static void ApplySettings(Plan p, InstalledInfo old)
        {
            // Wi-Fi standby: the policy is only reverted later if this installer changed it
            using (var k = Registry.LocalMachine.CreateSubKey(PolicyKey))
            {
                object v = k.GetValue("fMinimizeConnections");
                bool alreadyZero = v is int && (int)v == 0;
                if (p.Standby)
                {
                    if (!alreadyZero) { k.SetValue("fMinimizeConnections", 0, RegistryValueKind.DWord); p.StandbyByUs = true; Log("Set fMinimizeConnections=0"); }
                    else p.StandbyByUs = old != null && old.Standby;
                }
                else
                {
                    if (old != null && old.Standby) { k.DeleteValue("fMinimizeConnections", false); Log("Reverted fMinimizeConnections"); }
                    p.StandbyByUs = false;
                }
            }

            // Sign-in start goes into the signed-in user's own hive: when the elevated account differs from
            // that user, HKCU would point at the administrator instead
            string panel = Path.Combine(p.Dir, "NetWatchdogUI.exe");
            using (var k = OpenUserRunKey(p.PanelSid))
            {
                if (p.Autostart) k.SetValue("NetWatchdogUI", Q(panel) + " --tray");
                else k.DeleteValue("NetWatchdogUI", false);
            }

            foreach (var name in ShortcutNames)
                if (File.Exists(ShortcutPath(name))) File.Delete(ShortcutPath(name));
            if (p.Shortcut)
                Shortcut.Create(ShortcutPath(ShortcutName), panel,
                    L.T("View line status, pause or resume automatic switching", "查看网络线路状态，暂停或恢复自动切换"));
        }

        static RegistryKey OpenUserRunKey(string sid)
        {
            try
            {
                var k = Registry.Users.CreateSubKey(sid + "\\" + RunKey);
                if (k != null) return k;
            }
            catch (Exception ex) { Log("Cannot open the registry of user " + sid + ", using the current user: " + ex.Message); }
            return Registry.CurrentUser.CreateSubKey(RunKey);
        }

        static void RegisterApp(Plan p)
        {
            string setup = Path.Combine(p.Dir, "NetWatchdogSetup.exe");
            long bytes = ProgramFiles.Select(f => Path.Combine(p.Dir, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", L.T("NetWatchdog", "网络看门狗"));
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", "NetWatchdog");
                k.SetValue("DisplayIcon", Path.Combine(p.Dir, "NetWatchdogUI.exe") + ",0");
                k.SetValue("InstallLocation", p.Dir);
                k.SetValue("UninstallString", Q(setup) + " --uninstall");
                k.SetValue("QuietUninstallString", Q(setup) + " --uninstall --quiet");
                k.SetValue("Comments", L.T("Switches to a working line when the wired or Wi-Fi connection loses internet", "有线或 Wi-Fi 断网时自动切换到可用线路"));
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("NetWatchdogStandby", p.StandbyByUs ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("NetWatchdogAutostart", p.Autostart ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("NetWatchdogShortcut", p.Shortcut ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("NetWatchdogPanelSid", p.PanelSid ?? "");
            }
        }

        // ------------------------------------------------ Uninstall

        public static List<Step> UninstallSteps(Plan p, InstalledInfo old)
        {
            return new List<Step>
            {
                new Step(L.T("Stop the background task and tray panel", "停止后台任务和托盘面板"), () => StopRunning(true)),
                new Step(L.T("Restore network settings", "恢复网络设置"), () => RestoreNetwork(old)),
                new Step(L.T("Remove shortcuts and sign-in start", "删除快捷方式和登录启动项"), () => RemoveShortcuts(old)),
                new Step(L.T("Delete program files", "删除程序文件"), () => RemoveFiles(old, p.RemovePi)),
                new Step(p.RemoveData ? L.T("Delete settings and log", "删除设置和日志") : L.T("Clean up runtime state", "清理运行状态"), () => RemoveData(p.RemoveData)),
                new Step(L.T("Remove from Installed apps", "从“已安装的应用”中移除"), () => Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false)),
            };
        }

        static void RestoreNetwork(InstalledInfo old)
        {
            // Only interfaces whose metric equals the watchdog's demoted value are restored; manual metrics stay
            int metric = 9000;
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath, Encoding.UTF8)) as Dictionary<string, object>;
                object v;
                if (d != null && d.TryGetValue("demotedMetric", out v)) metric = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch { }
            string output;
            RunPowerShell("Get-NetIPInterface | Where-Object { $_.InterfaceMetric -eq " + metric +
                          " -and $_.AutomaticMetric -eq 'Disabled' } | Set-NetIPInterface -AutomaticMetric Enabled", out output);

            if (old.Standby)
            {
                using (var k = Registry.LocalMachine.OpenSubKey(PolicyKey, true))
                    if (k != null) k.DeleteValue("fMinimizeConnections", false);
                Log("Reverted fMinimizeConnections");
            }
        }

        static void RemoveShortcuts(InstalledInfo old)
        {
            foreach (var name in ShortcutNames)
                if (File.Exists(ShortcutPath(name))) File.Delete(ShortcutPath(name));
            var sids = new List<string> { WindowsIdentity.GetCurrent().User.Value };
            if (!string.IsNullOrEmpty(old.PanelSid)) sids.Add(old.PanelSid);
            foreach (var sid in sids.Distinct())
            {
                try
                {
                    using (var k = Registry.Users.OpenSubKey(sid + "\\" + RunKey, true))
                        if (k != null) { k.DeleteValue("NetWatchdogUI", false); k.DeleteValue("NetWatchdogAIReminder", false); }
                }
                catch (Exception ex) { Log("Cannot remove the sign-in start entry: " + ex.Message); }
            }
        }

        // Delete only our own files and remove the folder once it is empty, so a wrong path in the
        // registry can never delete anything else
        static void RemoveFiles(InstalledInfo old, bool removePi)
        {
            foreach (var f in ProgramFiles.Concat(new[] { "config.json" }))
            {
                string path = Path.Combine(old.Dir, f);
                if (File.Exists(path)) Retry(() => File.Delete(path));
            }
            // Pi, installed from the panel's AI tab into a fixed subfolder of the install folder; the uninstall
            // page asks whether to delete it (--keep-pi keeps it in quiet mode). A Pi folder chosen elsewhere in
            // the AI tab is never touched.
            string pi = Path.Combine(old.Dir, "pi");
            if (removePi && Directory.Exists(pi)) Retry(() => Directory.Delete(pi, true));
            if (Directory.Exists(old.Dir) && !Directory.EnumerateFileSystemEntries(old.Dir).Any()) Directory.Delete(old.Dir);
        }

        static void RemoveData(bool all)
        {
            if (!Directory.Exists(DataDir)) return;
            // Keeping settings keeps overrides.json: it holds the disabled lines, the pause state and panel settings,
            // which the panel and the README promise survive a reinstall
            var names = new List<string> { "status.json", "status.json.tmp" };
            if (all) names.AddRange(new[] { "overrides.json", "overrides.json.tmp", "config.json", "watchdog.log", "watchdog.log.1", "admin-allow.json", "admin-allow.json.tmp", "repair-state.json", "repair-state.json.tmp", "reboots.json", "repair.log" });
            foreach (var n in names)
            {
                string path = Path.Combine(DataDir, n);
                if (File.Exists(path)) Retry(() => File.Delete(path));
            }
            foreach (var d in new[] { "queue", "setup-tmp" })
            {
                string path = Path.Combine(DataDir, d);
                if (Directory.Exists(path)) Retry(() => Directory.Delete(path, true));
            }
            // AI configuration (including the API key) and logs; the Wi-Fi backup holds passwords in clear text
            if (all)
                foreach (var dir in new[] { "ai", "wifi-backup" })
                {
                    string path = Path.Combine(DataDir, dir);
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                }
            if (!Directory.EnumerateFileSystemEntries(DataDir).Any()) Directory.Delete(DataDir);
        }

        public static void ScheduleSelfDelete()
        {
            try
            {
                // The relocated copy sits alone in its own NetWatchdog-uninstall-* folder; remove that folder too
                string self = SetupProgram.SelfPath;
                string folder = Path.GetDirectoryName(self);
                string cmd = "/c ping 127.0.0.1 -n 3 > nul & del /f /q " + Q(self);
                if (Path.GetFileName(folder).StartsWith("NetWatchdog-uninstall-", StringComparison.OrdinalIgnoreCase)) cmd += " & rmdir /q " + Q(folder);
                Process.Start(new ProcessStartInfo("cmd.exe", cmd)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        // ------------------------------------------------ Helpers

        public static string Q(string s) { return "\"" + s + "\""; }

        public static string SidOf(string user)
        {
            try { return new NTAccount(user).Translate(typeof(SecurityIdentifier)).Value; }
            catch { return WindowsIdentity.GetCurrent().User.Value; }
        }

        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (IOException) { if (i >= 9) throw; Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { if (i >= 9) throw; Thread.Sleep(500); }
            }
        }

        static void Check(int code, string what)
        {
            if (code != 0)
                throw new InvalidOperationException(L.F("{0} failed (exit code {1}); see the setup log for details.",
                                                        "{0}失败（退出码 {1}），详情见安装日志。", what, code));
        }

        public static int Run(string file, string args)
        {
            string output;
            return Run(file, args, out output);
        }

        public static int Run(string file, string args, out string output, string display = null)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
                string e = err.Result;
                Log(string.Format("{0} {1} -> {2}{3}{4}", Path.GetFileName(file), display ?? args, p.ExitCode,
                    output.Trim().Length > 0 ? "\n  " + output.Trim() : "", e.Trim().Length > 0 ? "\n  " + e.Trim() : ""));
                return p.ExitCode;
            }
        }

        static int RunPowerShell(string script, out string output)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'; " + script));
            return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, out output, script);
        }
    }

    // ---------------------------------------------------------------- View models

    class SetupLineVM
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Glyph { get; set; }
        public Visibility DividerVisibility { get; set; }
    }

    class StepVM : Bindable
    {
        string glyph;
        Brush brush, textBrush;
        public string Name { get; set; }
        public string Glyph { get { return glyph; } set { Set(ref glyph, value); } }
        public Brush Brush { get { return brush; } set { Set(ref brush, value); } }
        public Brush TextBrush { get { return textBrush; } set { Set(ref textBrush, value); } }
    }

    class SetupVM : Bindable
    {
        string title, heading, subheading, description, linesHint, linesNote, installDir, installDirNote, uninstallNote;
        string stepText, doneTitle, doneDetail, doneGlyph, footerNote, primaryText, secondaryText;
        Visibility welcome = Visibility.Visible, progressV = Visibility.Collapsed, done = Visibility.Collapsed;
        Visibility installV = Visibility.Visible, uninstallV = Visibility.Collapsed, linesNoteV = Visibility.Visible;
        Visibility browseV = Visibility.Visible, launchV = Visibility.Collapsed, logLinkV = Visibility.Collapsed;
        Visibility primaryV = Visibility.Visible, secondaryV = Visibility.Visible;
        bool optStandby = true, optAutostart = true, optShortcut = true, optRemoveData, optLaunch = true, optRemovePi = true;
        Visibility piOptionV = Visibility.Collapsed;
        bool primaryEnabled = true, canClose = true;
        double progress;
        Brush doneBrush, doneSoft;

        public readonly ObservableCollection<SetupLineVM> LinesList = new ObservableCollection<SetupLineVM>();
        public readonly ObservableCollection<StepVM> StepsList = new ObservableCollection<StepVM>();
        public ObservableCollection<SetupLineVM> Lines { get { return LinesList; } }
        public ObservableCollection<StepVM> Steps { get { return StepsList; } }

        public string Title { get { return title; } set { Set(ref title, value); } }
        public string Heading { get { return heading; } set { Set(ref heading, value); } }
        public string Subheading { get { return subheading; } set { Set(ref subheading, value); } }
        public string Description { get { return description; } set { Set(ref description, value); } }
        public string LinesHint { get { return linesHint; } set { Set(ref linesHint, value); } }
        public string LinesNote { get { return linesNote; } set { Set(ref linesNote, value); } }
        public string InstallDir { get { return installDir; } set { Set(ref installDir, value); } }
        public string InstallDirNote { get { return installDirNote; } set { Set(ref installDirNote, value); } }
        public string UninstallNote { get { return uninstallNote; } set { Set(ref uninstallNote, value); } }
        public string StepText { get { return stepText; } set { Set(ref stepText, value); } }
        public string DoneTitle { get { return doneTitle; } set { Set(ref doneTitle, value); } }
        public string DoneDetail { get { return doneDetail; } set { Set(ref doneDetail, value); } }
        public string DoneGlyph { get { return doneGlyph; } set { Set(ref doneGlyph, value); } }
        public string FooterNote { get { return footerNote; } set { Set(ref footerNote, value); } }
        public string PrimaryText { get { return primaryText; } set { Set(ref primaryText, value); } }
        public string SecondaryText { get { return secondaryText; } set { Set(ref secondaryText, value); } }
        public Visibility WelcomeVisibility { get { return welcome; } set { Set(ref welcome, value); } }
        public Visibility ProgressVisibility { get { return progressV; } set { Set(ref progressV, value); } }
        public Visibility DoneVisibility { get { return done; } set { Set(ref done, value); } }
        public Visibility InstallVisibility { get { return installV; } set { Set(ref installV, value); } }
        public Visibility UninstallVisibility { get { return uninstallV; } set { Set(ref uninstallV, value); } }
        public Visibility LinesNoteVisibility { get { return linesNoteV; } set { Set(ref linesNoteV, value); } }
        public Visibility BrowseVisibility { get { return browseV; } set { Set(ref browseV, value); } }
        public Visibility LaunchVisibility { get { return launchV; } set { Set(ref launchV, value); } }
        public Visibility LogLinkVisibility { get { return logLinkV; } set { Set(ref logLinkV, value); } }
        public Visibility PrimaryVisibility { get { return primaryV; } set { Set(ref primaryV, value); } }
        public Visibility SecondaryVisibility { get { return secondaryV; } set { Set(ref secondaryV, value); } }
        public bool OptStandby { get { return optStandby; } set { Set(ref optStandby, value); } }
        public bool OptAutostart { get { return optAutostart; } set { Set(ref optAutostart, value); } }
        public bool OptShortcut { get { return optShortcut; } set { Set(ref optShortcut, value); } }
        public bool OptRemoveData { get { return optRemoveData; } set { Set(ref optRemoveData, value); } }
        public bool OptRemovePi { get { return optRemovePi; } set { Set(ref optRemovePi, value); } }
        public Visibility PiOptionVisibility { get { return piOptionV; } set { Set(ref piOptionV, value); } }
        public bool OptLaunch { get { return optLaunch; } set { Set(ref optLaunch, value); } }
        public bool PrimaryEnabled { get { return primaryEnabled; } set { Set(ref primaryEnabled, value); } }
        public bool CanClose { get { return canClose; } set { Set(ref canClose, value); } }
        public double Progress { get { return progress; } set { Set(ref progress, value); } }
        public Brush DoneBrush { get { return doneBrush; } set { Set(ref doneBrush, value); } }
        public Brush DoneSoft { get { return doneSoft; } set { Set(ref doneSoft, value); } }
    }

    // ---------------------------------------------------------------- Wizard

    enum Mode { Install, Upgrade, Reinstall, Repair, Uninstall }

    class SetupController
    {
        const string GlyphPending = "\uECCA", GlyphRunning = "\uE895", GlyphDone = "\uE73E", GlyphFailed = "\uE711";

        readonly SetupArgs args;
        readonly SetupVM vm = new SetupVM();
        readonly Plan plan = new Plan();
        Theme theme;
        Window win;
        InstalledInfo old;
        Mode mode;
        bool running, finished, succeeded;
        public int ExitCode = 0;

        public SetupController(SetupArgs a) { args = a; }

        static Mode DecideMode(SetupArgs a, InstalledInfo old)
        {
            if (a.Uninstall) return Mode.Uninstall;
            if (old == null) return Mode.Install;
            if (a.Repair) return Mode.Repair;
            return old.Version == AppInfo.Version ? Mode.Reinstall : Mode.Upgrade;
        }

        static void FillPlan(Plan p, SetupArgs a, InstalledInfo old)
        {
            p.User = a.User;
            p.Dir = old != null ? old.Dir : (a.Dir ?? Installer.DefaultDir);
            p.RemovePi = !a.KeepPi;
            if (old != null)
            {
                p.Autostart = old.Autostart;
                p.Shortcut = old.Shortcut;
                p.Standby = old.Standby || CurrentStandby();
            }
        }

        static bool CurrentStandby()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy"))
            {
                object v = k == null ? null : k.GetValue("fMinimizeConnections");
                return v is int && (int)v == 0;
            }
        }

        // Quiet mode: default or existing settings; returns 0 on success
        public static int RunQuiet(SetupArgs a)
        {
            var old = Installer.FindInstalled();
            var mode = DecideMode(a, old);
            var p = new Plan();
            FillPlan(p, a, old);
            List<Step> steps;
            if (mode == Mode.Uninstall)
            {
                if (old == null) return 0;
                steps = Installer.UninstallSteps(p, old);
            }
            else
            {
                p.Lines = Installer.ReadConfiguredLines() ?? Installer.DetectLines();
                steps = Installer.InstallSteps(p, old);
            }
            foreach (var s in steps)
            {
                try { Installer.Log("== " + s.Name); s.Run(); }
                catch (Exception ex) { Installer.Log(ex.ToString()); return 1; }
            }
            return 0;
        }

        public void Start()
        {
            Installer.Log("==== NetWatchdog Setup " + AppInfo.Version + ", arguments: " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
            theme = Theme.Load(args.ForceDark);
            win = (Window)Xaml.Load("NetWatchdogUI.Setup.xaml");
            L.Apply(win);
            ((TextBlock)win.FindName("HeadingText")).FontFamily = new FontFamily(L.Current.Font.Replace("Variable Text", "Variable Display"));
            theme.Apply(win.Resources);
            win.DataContext = vm;
            win.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
            win.SourceInitialized += (s, e) => Native.RoundCorners(new WindowInteropHelper(win).Handle);
            win.Closing += (s, e) => { if (running) e.Cancel = true; };
            win.Closed += (s, e) => Application.Current.Shutdown();
            SetIcon();

            // Snapshots ignore what is installed on this machine: a fresh install, or an uninstall of this version
            old = args.Snapshot == null ? Installer.FindInstalled()
                : args.Uninstall ? new InstalledInfo { Dir = Installer.DefaultDir, Version = AppInfo.Version, Standby = true }
                : null;
            mode = DecideMode(args, old);
            FillPlan(plan, args, old);
            ConfigureWelcome();

            if (args.Snapshot != null) { StartSnapshot(); return; }

            win.Show();
            if (mode == Mode.Uninstall && old == null)
                Finish(new InvalidOperationException(L.T("NetWatchdog is not installed on this computer.", "这台电脑上没有安装网络看门狗。")));
            else if (mode == Mode.Repair)
                Begin();
            else if (mode != Mode.Uninstall)
                DetectLinesAsync();
        }

        void SetIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetWatchdogUI.app.ico"))
                {
                    var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var big = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                    ((Image)win.FindName("AppIcon")).Source = big;
                    win.Icon = dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).First();
                }
            }
            catch (Exception ex) { Installer.Log("Cannot load the icon: " + ex.Message); }
        }

        // ------------------------------------------------ Pages

        void ConfigureWelcome()
        {
            vm.Heading = L.T("NetWatchdog", "网络看门狗");
            vm.Subheading = L.F("Version {0} · automatic wired and Wi-Fi failover", "版本 {0} · 有线与 Wi-Fi 自动切换", AppInfo.Version);
            vm.InstallDir = plan.Dir;
            vm.InstallDirNote = L.F("Config and log are kept in {0}", "配置和日志保存在 {0}", Installer.DataDir);
            vm.OptStandby = plan.Standby;
            vm.OptAutostart = plan.Autostart;
            vm.OptShortcut = plan.Shortcut;
            vm.SecondaryText = L.T("Cancel", "取消");
            vm.BrowseVisibility = mode == Mode.Install ? Visibility.Visible : Visibility.Collapsed;
            vm.FooterNote = L.T("Requires administrator rights", "需要管理员权限");

            switch (mode)
            {
                case Mode.Install:
                    vm.Title = L.T("Install NetWatchdog", "安装网络看门狗");
                    vm.Description = L.T(
                        "NetWatchdog keeps checking whether the wired and Wi-Fi connections can reach the internet. When the current line loses internet it moves to a working line, and it moves back once the line recovers. The tray panel shows the latency of every line and lets you disable a line or pause automatic switching at any time.",
                        "网络看门狗在后台持续检测有线和 Wi-Fi 的外网连通性。当前线路断网时，它自动换到可用的线路，恢复后再切回。托盘面板显示每条线路的延迟，可以随时停用某条线路或暂停自动切换。");
                    vm.PrimaryText = L.T("Install", "安装");
                    break;
                case Mode.Upgrade:
                    vm.Title = L.T("Upgrade NetWatchdog", "升级网络看门狗");
                    vm.Description = L.F("Version {0} is installed and will be upgraded to {1}. The existing config, disabled lines and log are kept.",
                                         "已安装版本 {0}，将升级到 {1}。现有的配置、停用的线路和日志都会保留。", old.Version, AppInfo.Version);
                    vm.PrimaryText = L.T("Upgrade", "升级");
                    break;
                case Mode.Reinstall:
                case Mode.Repair:
                    vm.Title = mode == Mode.Repair ? L.T("Repair NetWatchdog", "修复网络看门狗") : L.T("Reinstall NetWatchdog", "重新安装网络看门狗");
                    vm.Description = L.F("Version {0} is already installed. Reinstalling repairs the program files, folder permissions and background task; the config is kept.",
                                         "已安装同一版本 {0}。重新安装会修复程序文件、目录权限和后台任务，配置保持不变。", AppInfo.Version);
                    vm.PrimaryText = L.T("Reinstall", "重新安装");
                    break;
                case Mode.Uninstall:
                    vm.Title = L.T("Uninstall NetWatchdog", "卸载网络看门狗");
                    vm.Description = L.T(
                        "Uninstalling stops the background watchdog, restores the adapter metrics it changed to automatic, and deletes the program files, the Start menu shortcut and the sign-in start entry.",
                        "卸载会停止后台看门狗，把它调整过的网卡跃点数恢复为自动，并删除程序文件、开始菜单快捷方式和登录启动项。");
                    vm.UninstallNote = old != null && old.Standby
                        ? L.T("The Wi-Fi standby enabled during installation is turned off as well, restoring the default Windows connection policy after a restart.",
                              "安装时开启的 Wi-Fi 热备会一并关闭，恢复 Windows 默认的连接管理策略，重启后生效。")
                        : "";
                    vm.PrimaryText = L.T("Uninstall", "卸载");
                    vm.InstallVisibility = Visibility.Collapsed;
                    vm.UninstallVisibility = Visibility.Visible;
                    // Asked only when the panel installed Pi into the install folder
                    vm.PiOptionVisibility = old != null && Directory.Exists(Path.Combine(old.Dir, "pi")) ? Visibility.Visible : Visibility.Collapsed;
                    vm.FooterNote = L.F("Version {0}", "版本 {0}", old != null ? old.Version : AppInfo.Version);
                    break;
            }
        }

        void DetectLinesAsync()
        {
            var configured = Installer.ReadConfiguredLines();
            if (configured != null)
            {
                ShowLines(configured, L.T("From the existing config", "沿用现有配置"));
                return;
            }
            vm.LinesNote = L.T("Detecting network adapters…", "正在检测网卡…");
            vm.PrimaryEnabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var lines = Installer.DetectLines();
                win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowLines(lines, L.T("In priority order", "按优先级排列"));
                    vm.PrimaryEnabled = true;
                }));
            });
        }

        void ShowLines(List<DetectedLine> lines, string hint)
        {
            plan.Lines = lines;
            vm.LinesList.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                bool wifi = lines[i].Type == "wifi";
                vm.LinesList.Add(new SetupLineVM
                {
                    Name = lines[i].Name,
                    Detail = (wifi ? L.T("Wireless", "无线") : L.T("Wired", "有线")) + " · " +
                             (i == 0 ? L.T("preferred", "优先使用") : L.T("backup", "备用")),
                    Glyph = wifi ? "\uE701" : "\uE839",
                    DividerVisibility = i == 0 ? Visibility.Collapsed : Visibility.Visible
                });
            }
            vm.LinesHint = lines.Count > 0 ? hint : "";
            if (lines.Count == 0)
                vm.LinesNote = L.F("No wired or wireless adapter was found. You can still install and fill in adapter names in {0} later.",
                                   "没有检测到有线或无线网卡。仍然可以安装，之后在 {0} 里填写网卡名称。", Installer.ConfigPath);
            vm.LinesNoteVisibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void ShowPage(int page)
        {
            vm.WelcomeVisibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
            vm.ProgressVisibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
            vm.DoneVisibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------ Running the steps

        void Begin()
        {
            plan.Standby = vm.OptStandby;
            plan.Autostart = vm.OptAutostart;
            plan.Shortcut = vm.OptShortcut;
            plan.RemoveData = vm.OptRemoveData;
            plan.RemovePi = vm.OptRemovePi;
            plan.Dir = vm.InstallDir;
            if (plan.Lines == null || (plan.Lines.Count == 0 && mode != Mode.Uninstall)) plan.Lines = Installer.ReadConfiguredLines() ?? new List<DetectedLine>();

            var steps = mode == Mode.Uninstall ? Installer.UninstallSteps(plan, old) : Installer.InstallSteps(plan, old);
            vm.StepsList.Clear();
            foreach (var s in steps)
                vm.StepsList.Add(new StepVM { Name = s.Name, Glyph = GlyphPending, Brush = theme["Neutral"], TextBrush = theme["SubText"] });

            running = true;
            vm.CanClose = false;
            vm.PrimaryVisibility = Visibility.Collapsed;
            vm.SecondaryVisibility = Visibility.Collapsed;
            vm.FooterNote = L.T("Please wait, do not close this window", "请稍候，不要关闭这个窗口");
            ShowPage(1);

            var thread = new Thread(() =>
            {
                Exception error = null;
                for (int i = 0; i < steps.Count; i++)
                {
                    int n = i;
                    Ui(() =>
                    {
                        vm.StepText = steps[n].Name + "…";
                        vm.Progress = 100.0 * n / steps.Count;
                        vm.StepsList[n].Glyph = GlyphRunning;
                        vm.StepsList[n].Brush = theme["Accent"];
                        vm.StepsList[n].TextBrush = theme["Text"];
                    });
                    try
                    {
                        Installer.Log("== " + steps[n].Name);
                        steps[n].Run();
                    }
                    catch (Exception ex)
                    {
                        Installer.Log(ex.ToString());
                        error = ex;
                        Ui(() => { vm.StepsList[n].Glyph = GlyphFailed; vm.StepsList[n].Brush = theme["Bad"]; });
                        break;
                    }
                    Ui(() => { vm.StepsList[n].Glyph = GlyphDone; vm.StepsList[n].Brush = theme["Good"]; });
                    Thread.Sleep(120);
                }
                Ui(() => Finish(error));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void Ui(Action a) { win.Dispatcher.Invoke(a); }

        void Finish(Exception error)
        {
            running = false;
            finished = true;
            succeeded = error == null;
            ExitCode = succeeded ? 0 : 1;
            vm.CanClose = true;
            vm.Progress = 100;
            vm.PrimaryVisibility = Visibility.Visible;
            vm.SecondaryVisibility = Visibility.Collapsed;
            vm.FooterNote = "";
            bool uninstall = mode == Mode.Uninstall;

            if (succeeded)
            {
                vm.DoneGlyph = GlyphDone;
                vm.DoneBrush = theme["Good"];
                vm.DoneSoft = theme["GoodSoft"];
                vm.PrimaryText = L.T("Done", "完成");
                if (uninstall)
                {
                    vm.DoneTitle = L.T("Uninstalled", "已卸载");
                    vm.DoneDetail = L.T("NetWatchdog has been removed from this computer.", "网络看门狗已从这台电脑上移除。") +
                        (plan.RemoveData ? "" : L.F(" Settings and log stay in {0} and are reused if you install again.",
                                                    "设置和日志保留在 {0}，重新安装时会沿用。", Installer.DataDir));
                }
                else
                {
                    vm.DoneTitle = mode == Mode.Upgrade ? L.T("Upgrade complete", "升级完成")
                                 : mode == Mode.Install ? L.T("Installation complete", "安装完成")
                                 : L.T("Repair complete", "修复完成");
                    vm.DoneDetail = L.T("The watchdog is running in the background and starts with Windows. Its tray icon sits at the right end of the taskbar; click it to see every line.",
                                        "看门狗已在后台运行，开机自动启动。托盘图标在任务栏右侧，点它查看每条线路的状态。") +
                        (plan.StandbyByUs ? L.T(" Wi-Fi standby takes effect after a restart.", "Wi-Fi 热备在重启后生效。") : "");
                    vm.LaunchVisibility = Visibility.Visible;
                }
            }
            else
            {
                vm.DoneGlyph = GlyphFailed;
                vm.DoneBrush = theme["Bad"];
                vm.DoneSoft = theme["BadSoft"];
                vm.DoneTitle = uninstall ? L.T("Uninstall did not finish", "卸载没有完成") : L.T("Installation did not finish", "安装没有完成");
                vm.DoneDetail = error.Message + (old != null || !uninstall
                    ? L.T("\nCompleted steps are not rolled back; run the setup package again to repair.", "\n已完成的步骤不会回滚，重新运行安装程序即可修复。")
                    : "");
                vm.LogLinkVisibility = Visibility.Visible;
                vm.PrimaryText = L.T("Close", "关闭");
            }
            ShowPage(2);
        }

        // The installer runs elevated; starting the panel through Explorer gives it the signed-in user's normal rights
        void LaunchPanel()
        {
            string panel = Path.Combine(plan.Dir, "NetWatchdogUI.exe");
            try { Process.Start("explorer.exe", Installer.Q(panel)); }
            catch (Exception ex) { Installer.Log("Cannot start the tray panel: " + ex.Message); }
        }

        void OnClick(object sender, RoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            string tag = fe == null ? null : fe.Tag as string;
            switch (tag)
            {
                case "minimize": win.WindowState = WindowState.Minimized; break;
                case "close":
                case "secondary":
                    win.Close();
                    break;
                case "primary":
                    if (!finished) Begin();
                    else
                    {
                        if (succeeded && mode != Mode.Uninstall && vm.OptLaunch) LaunchPanel();
                        win.Close();
                    }
                    break;
                case "browse": Browse(); break;
                case "openlog":
                    try { Process.Start("notepad.exe", Installer.Q(Installer.LogPath)); } catch { }
                    break;
            }
        }

        void Browse()
        {
            using (var dlg = new WF.FolderBrowserDialog())
            {
                dlg.Description = L.T("Choose where to install; the program goes into a NetWatchdog folder inside it",
                                      "选择安装位置，程序会装在其中的 NetWatchdog 文件夹里");
                dlg.SelectedPath = Path.GetDirectoryName(vm.InstallDir);
                if (dlg.ShowDialog() != WF.DialogResult.OK) return;
                string dir = dlg.SelectedPath;
                if (!string.Equals(Path.GetFileName(dir.TrimEnd('\\')), "NetWatchdog", StringComparison.OrdinalIgnoreCase))
                    dir = Path.Combine(dir, "NetWatchdog");
                vm.InstallDir = dir;
            }
        }

        // ------------------------------------------------ Snapshots (for debugging and docs)

        void StartSnapshot()
        {
            switch (args.Page)
            {
                case "progress":
                    ShowLines(Installer.DetectLines(), L.T("In priority order", "按优先级排列"));
                    var steps = mode == Mode.Uninstall ? Installer.UninstallSteps(plan, old) : Installer.InstallSteps(plan, old);
                    for (int i = 0; i < steps.Count; i++)
                    {
                        bool doneStep = i < 3, current = i == 3;
                        vm.StepsList.Add(new StepVM
                        {
                            Name = steps[i].Name,
                            Glyph = doneStep ? GlyphDone : current ? GlyphRunning : GlyphPending,
                            Brush = doneStep ? theme["Good"] : current ? theme["Accent"] : theme["Neutral"],
                            TextBrush = doneStep || current ? theme["Text"] : theme["SubText"]
                        });
                    }
                    vm.StepText = steps[3].Name + "…";
                    vm.Progress = 55;
                    vm.PrimaryVisibility = Visibility.Collapsed;
                    vm.SecondaryVisibility = Visibility.Collapsed;
                    vm.CanClose = false;
                    vm.FooterNote = L.T("Please wait, do not close this window", "请稍候，不要关闭这个窗口");
                    ShowPage(1);
                    break;
                case "done":
                    plan.StandbyByUs = true;
                    Finish(null);
                    break;
                case "error":
                    Finish(new InvalidOperationException(L.T("Registering the scheduled task failed (exit code 1); see the setup log for details.",
                                                             "注册计划任务失败（退出码 1），详情见安装日志。")));
                    break;
                default:
                    if (mode != Mode.Uninstall) ShowLines(Installer.DetectLines(), L.T("In priority order", "按优先级排列"));
                    break;
            }
            win.Topmost = true;
            win.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    Native.SaveWindow(new WindowInteropHelper(win).Handle, args.Snapshot);
                }
                catch (Exception ex) { Installer.Log(ex.ToString()); }
                win.Close();
            };
            t.Start();
        }
    }
}
