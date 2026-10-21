// NetRepair: a command-line port of the network repairs in "NetAdapter Repair All In One"
// by Conner Bernhard (https://github.com/Archer1613/netadapter-repair, MIT License, see LICENSE in this folder).
//
// The original is a WinForms tool whose repairs run behind buttons and confirmation dialogs, so it cannot be
// called by another program. This port keeps the same commands, runs without any window and reports through
// stdout, a log file and the exit code, so the watchdog can call it while the network is down.
//
// Usage:
//   NetRepair.exe <action> [<action> ...] [--adapter <name>] [--log <file>] [--dry-run]
//   NetRepair.exe --list
// Exit code: 0 = every command succeeded, 1 = at least one command reported an error, 2 = bad arguments.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("NetRepair (command-line NetAdapter Repair)")]
[assembly: System.Reflection.AssemblyProduct("NetWatchdog")]

namespace NetRepair
{
    static class Program
    {
        static StreamWriter log;
        static bool dryRun;
        static int failures;

        // id, description, needs a restart to take full effect
        static readonly string[][] Actions =
        {
            new[] { "flushdns", "Flush and re-register the DNS cache", "no" },
            new[] { "arp", "Clear the ARP, neighbor and destination caches", "no" },
            new[] { "renew", "Release and renew the DHCP address (one adapter with --adapter, otherwise all)", "no" },
            new[] { "netbios", "Purge and reload the NetBIOS name cache", "no" },
            new[] { "ssl", "Clear the SSL state cache", "no" },
            new[] { "proxy", "Reset WinHTTP proxy and turn off the system proxy for signed-in users", "no" },
            new[] { "dhcp", "Switch the adapter (--adapter) or all adapters back to DHCP for address and DNS", "no" },
            new[] { "hosts", "Restore the default hosts file (the current one is backed up next to it)", "no" },
            new[] { "tcpip", "Reset the TCP/IP stack and Winsock catalog", "yes" },
        };

        static int Main(string[] args)
        {
            var todo = new List<string>();
            string adapter = null, logPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--list") { foreach (var x in Actions) Console.WriteLine("{0,-9} {1}{2}", x[0], x[1], x[2] == "yes" ? " (restart needed)" : ""); return 0; }
                else if (a == "--dry-run") dryRun = true;
                else if (a == "--adapter" && i + 1 < args.Length) adapter = args[++i];
                else if (a == "--log" && i + 1 < args.Length) logPath = args[++i];
                else if (Actions.Any(x => x[0] == a)) todo.Add(a);
                else { Console.Error.WriteLine("Unknown argument: " + a + " (use --list)"); return 2; }
            }
            if (todo.Count == 0) { Console.Error.WriteLine("No action given (use --list)"); return 2; }

            if (logPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
                log = new StreamWriter(logPath, true, new UTF8Encoding(false));
            }
            try
            {
                foreach (var id in todo)
                {
                    Log("== " + id + (adapter != null ? " (" + adapter + ")" : ""));
                    switch (id)
                    {
                        case "flushdns": FlushDns(); break;
                        case "arp": ClearArp(); break;
                        case "renew": Renew(adapter); break;
                        case "netbios": NetBios(); break;
                        case "ssl": ClearSsl(); break;
                        case "proxy": ResetProxy(); break;
                        case "dhcp": SetDhcp(adapter); break;
                        case "hosts": ResetHosts(); break;
                        case "tcpip": ResetTcpIp(); break;
                    }
                }
            }
            finally { if (log != null) log.Dispose(); }
            return failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------ helpers

        static void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg;
            Console.WriteLine(line);
            if (log != null) { log.WriteLine(line); log.Flush(); }
        }

        static string Sys(string exe) { return Path.Combine(Environment.SystemDirectory, exe); }

        // Same rule as the original: a command fails when its output contains "error "
        static void Run(string exe, string args)
        {
            if (dryRun) { Log("  would run: " + exe + " " + args); return; }
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } }
                    bool failed = output.ToLowerInvariant().Contains("error ") || (p.HasExited && p.ExitCode != 0);
                    Log("  " + Path.GetFileName(exe) + " " + args + (failed ? "  -> FAILED (exit " + p.ExitCode + ")" : "  -> ok"));
                    if (failed) { failures++; foreach (var l in output.Trim().Split('\n').Take(5)) Log("    " + l.TrimEnd('\r')); }
                }
            }
            catch (Exception ex) { failures++; Log("  " + exe + " " + args + "  -> FAILED: " + ex.Message); }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        // ------------------------------------------------ repairs (commands as in NetAdapter Repair)

        static void FlushDns()
        {
            Run(Sys("ipconfig.exe"), "/flushdns");
            Run(Sys("ipconfig.exe"), "/registerdns");
        }

        static void ClearArp()
        {
            Run(Sys("netsh.exe"), "interface ip delete arpcache");
            Run(Sys("netsh.exe"), "interface ipv4 delete destinationcache");
            Run(Sys("netsh.exe"), "interface ipv6 delete destinationcache");
            Run(Sys("netsh.exe"), "interface ipv4 delete neighbors");
            Run(Sys("netsh.exe"), "interface ipv6 delete neighbors");
        }

        static void Renew(string adapter)
        {
            string target = adapter != null ? " " + Q(adapter) : "";
            Run(Sys("ipconfig.exe"), "/release" + target);
            Run(Sys("ipconfig.exe"), "/renew" + target);
        }

        static void NetBios()
        {
            Run(Sys("nbtstat.exe"), "-R");
            Run(Sys("nbtstat.exe"), "-RR");
        }

        static void ClearSsl()
        {
            Run(Sys("rundll32.exe"), Q(Sys("wininet.dll")) + ",DispatchAPICall 3");
        }

        // The original edits HKCU. Run as SYSTEM, HKCU is SYSTEM's own hive, so every loaded user hive is updated.
        static void ResetProxy()
        {
            Run(Sys("netsh.exe"), "winhttp reset proxy");
            foreach (var sid in Registry.Users.GetSubKeyNames().Where(n => n.StartsWith("S-1-5-21-") && !n.EndsWith("_Classes")))
            {
                string path = sid + @"\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                if (dryRun) { Log("  would turn off the proxy for " + sid); continue; }
                try
                {
                    using (var k = Registry.Users.OpenSubKey(path, true))
                    {
                        if (k == null) continue;
                        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        k.DeleteValue("ProxyServer", false);
                        k.DeleteValue("AutoConfigURL", false);
                        Log("  turned off the system proxy for " + sid + "  -> ok");
                    }
                }
                catch (Exception ex) { failures++; Log("  proxy for " + sid + "  -> FAILED: " + ex.Message); }
            }
        }

        static void SetDhcp(string adapter)
        {
            var names = new List<string>();
            if (adapter != null) names.Add(adapter);
            else
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    var t = nic.NetworkInterfaceType;
                    if (t == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                        t == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) names.Add(nic.Name);
                }
            }
            foreach (var n in names)
            {
                Run(Sys("netsh.exe"), "interface ip set address " + Q(n) + " source=dhcp");
                Run(Sys("netsh.exe"), "interface ip set dns " + Q(n) + " source=dhcp");
            }
        }

        static void ResetHosts()
        {
            string hosts = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
            string backup = hosts + ".netrepair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            if (dryRun) { Log("  would back up the hosts file to " + backup + " and restore the default"); return; }
            try
            {
                if (File.Exists(hosts))
                {
                    File.SetAttributes(hosts, FileAttributes.Normal);
                    File.Copy(hosts, backup, true);
                    Log("  backed up the hosts file to " + backup);
                }
                File.WriteAllText(hosts,
                    "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
                    "#\r\n" +
                    "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n" +
                    "#\r\n" +
                    "# localhost name resolution is handled within DNS itself.\r\n" +
                    "#\t127.0.0.1       localhost\r\n" +
                    "#\t::1             localhost\r\n", Encoding.ASCII);
                Log("  restored the default hosts file  -> ok");
            }
            catch (Exception ex) { failures++; Log("  hosts  -> FAILED: " + ex.Message); }
        }

        static void ResetTcpIp()
        {
            Run(Sys("netsh.exe"), "int ip reset");
            Run(Sys("netsh.exe"), "int tcp reset all");
            Run(Sys("netsh.exe"), "winhttp reset proxy");
            Run(Sys("netsh.exe"), "winsock reset");
        }
    }
}
