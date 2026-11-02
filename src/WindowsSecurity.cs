using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Maela
{
    internal enum ElevatedResult
    {
        Done,
        Cancelled,       // The Windows approval prompt was declined.
        DidNotPersist,   // It ran, but the change isn't there (Tamper Protection, usually).
        Unavailable,     // Defender isn't the active antivirus, or the command failed.
        Failed
    }

    /// <summary>
    /// The two Windows settings that can stop mæla working: a firewall rule, so friends and room
    /// members can reach you, and a Defender exclusion for false positives. Both change only this
    /// exe's path, and both go through the Windows approval prompt.
    /// </summary>
    internal static class WindowsSecurity
    {
        private const string RuleName = "Maela Room";
        private const int ExitNotPersisted = 3;
        private const int ExitNoDefender = 4;

        /// <summary>
        /// True if inbound traffic to this exe is allowed. Reading rules needs no approval. If they
        /// can't be read, this says true, so nobody is prompted over a guess.
        /// </summary>
        public static bool FirewallAllows(string exePath)
        {
            try
            {
                Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                object policy = Activator.CreateInstance(type);
                object rules = Get(policy, "Rules");
                bool allowed = false;
                foreach (object rule in (IEnumerable)rules)
                {
                    string application = Get(rule, "ApplicationName") as string;
                    if (application == null || !String.Equals(application, exePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!(bool)Get(rule, "Enabled") || (int)Get(rule, "Direction") != 1)
                        continue;
                    if ((int)Get(rule, "Action") == 0)
                        return false; // A block rule beats any allow rule.
                    int protocol = (int)Get(rule, "Protocol");
                    if (protocol == 17 || protocol == 256)
                        allowed = true;
                }
                return allowed;
            }
            catch
            {
                return true;
            }
        }

        private static object Get(object target, string property)
        {
            return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);
        }

        /// <summary>Replaces this exe's inbound rules with one that allows UDP. Other copies' rules are left alone.</summary>
        public static ElevatedResult AllowThroughFirewall(string exePath)
        {
            string path = exePath.Replace("\"", String.Empty);
            string command = "/c netsh advfirewall firewall delete rule name=all dir=in program=\"" + path + "\" >nul 2>&1 & netsh advfirewall firewall add rule name=\"" + RuleName +
                "\" dir=in action=allow program=\"" + path + "\" protocol=UDP enable=yes profile=any";
            return RunElevated("cmd.exe", command);
        }

        /// <summary>Removes this exe's inbound rules. Other copies' rules are left alone.</summary>
        public static ElevatedResult RemoveFromFirewall(string exePath)
        {
            string path = exePath.Replace("\"", String.Empty);
            return RunElevated("netsh.exe", "advfirewall firewall delete rule name=all dir=in program=\"" + path + "\"");
        }

        /// <summary>Adds or removes a Defender exclusion for this one file, then checks what actually saved.</summary>
        public static ElevatedResult SetDefenderExclusion(string exePath, bool exclude)
        {
            return RunElevated("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Encode(DefenderScript(exePath, exclude)));
        }

        internal static string DefenderScript(string exePath, bool exclude)
        {
            string path = exePath.Replace("'", "''");
            return "try { " + (exclude ? "Add" : "Remove") + "-MpPreference -ExclusionPath '" + path + "' -ErrorAction Stop } catch { exit " + ExitNoDefender + " }; " +
                "$now = (Get-MpPreference).ExclusionPath; " +
                "if (($now -contains '" + path + "') -eq $" + (exclude ? "true" : "false") + ") { exit 0 } else { exit " + ExitNotPersisted + " }";
        }

        internal static string Encode(string script)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        private static ElevatedResult RunElevated(string file, string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = file;
                info.Arguments = arguments;
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process process = Process.Start(info))
                {
                    if (process == null)
                        return ElevatedResult.Failed;
                    process.WaitForExit();
                    switch (process.ExitCode)
                    {
                        case 0: return ElevatedResult.Done;
                        case ExitNotPersisted: return ElevatedResult.DidNotPersist;
                        case ExitNoDefender: return ElevatedResult.Unavailable;
                        default: return ElevatedResult.Failed;
                    }
                }
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? ElevatedResult.Cancelled : ElevatedResult.Failed;
            }
            catch
            {
                return ElevatedResult.Failed;
            }
        }
    }
}
