using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Playnite.SDK;

namespace SaveVault.Services
{
    /// <summary>
    /// Registry side of a snapshot. Export and import go through reg.exe because a .reg
    /// file is human readable, survives without this plugin, and can be inspected before
    /// it is applied. Nothing here runs unless the user opts in.
    /// </summary>
    public static class RegistryBridge
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>
        /// Manifest keys use forward slashes and short hive names. reg.exe needs backslashes
        /// and accepts both long and short hive names, so only the separators and a few
        /// aliases have to be fixed.
        /// </summary>
        public static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var text = key.Trim().Replace('/', '\\').TrimEnd('\\');
            var upperFirst = text.Split('\\').FirstOrDefault();
            if (string.IsNullOrEmpty(upperFirst))
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "HKCU", "HKEY_CURRENT_USER" },
                { "HKLM", "HKEY_LOCAL_MACHINE" },
                { "HKCR", "HKEY_CLASSES_ROOT" },
                { "HKU", "HKEY_USERS" }
            };

            string full;
            if (map.TryGetValue(upperFirst, out full))
            {
                text = full + text.Substring(upperFirst.Length);
            }

            return text;
        }

        /// <summary>True when the key exists for the current user.</summary>
        public static bool Exists(string key)
        {
            var normalized = NormalizeKey(key);
            if (normalized == null)
            {
                return false;
            }

            var split = normalized.IndexOf('\\');
            if (split < 0)
            {
                return false;
            }

            var hiveName = normalized.Substring(0, split);
            var subKey = normalized.Substring(split + 1);

            try
            {
                using (var hive = OpenHive(hiveName))
                {
                    if (hive == null)
                    {
                        return false;
                    }

                    using (var opened = hive.OpenSubKey(subKey, false))
                    {
                        return opened != null;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static RegistryKey OpenHive(string name)
        {
            switch (name.ToUpperInvariant())
            {
                case "HKEY_CURRENT_USER":
                    return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                case "HKEY_LOCAL_MACHINE":
                    return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                case "HKEY_CLASSES_ROOT":
                    return RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
                case "HKEY_USERS":
                    return RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
                default:
                    return null;
            }
        }

        /// <summary>Writes a UTF-16 .reg file for the key. Returns false when nothing was written.</summary>
        public static bool Export(string key, string file)
        {
            var normalized = NormalizeKey(key);
            if (normalized == null || !Exists(normalized))
            {
                return false;
            }

            try
            {
                var folder = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                var ok = Run("export \"" + normalized + "\" \"" + file + "\" /y");
                return ok && File.Exists(file) && new FileInfo(file).Length > 0;
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: registry export failed for " + normalized);
                return false;
            }
        }

        /// <summary>Applies a previously exported .reg file.</summary>
        public static bool Import(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                return false;
            }

            return Run("import \"" + file + "\"");
        }

        private static bool Run(string arguments)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(start))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    var error = process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit(30000);

                    if (process.ExitCode != 0)
                    {
                        logger.Warn("Save Vault: reg.exe " + arguments + " exited with " + process.ExitCode + " " + error.Trim());
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: could not run reg.exe " + arguments);
                return false;
            }
        }

        /// <summary>
        /// Shallow search of HKCU\Software for keys whose name looks like the game, its
        /// developer or its publisher. Two levels deep covers the usual vendor\title shape
        /// without walking the entire hive.
        /// </summary>
        public static IEnumerable<string> Guess(IEnumerable<string> tokens)
        {
            var results = new List<string>();
            var wanted = tokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(LudusaviManifest.NormalizeName)
                .Where(t => t.Length >= 3)
                .Distinct()
                .ToList();

            if (wanted.Count == 0)
            {
                return results;
            }

            try
            {
                using (var software = Registry.CurrentUser.OpenSubKey("Software", false))
                {
                    if (software == null)
                    {
                        return results;
                    }

                    foreach (var vendorName in Names(software))
                    {
                        var vendorKey = LudusaviManifest.NormalizeName(vendorName);
                        var vendorHit = vendorKey.Length >= 3 && wanted.Any(w => w == vendorKey);

                        if (vendorHit)
                        {
                            results.Add("HKEY_CURRENT_USER\\Software\\" + vendorName);
                            continue;
                        }

                        using (var vendor = SafeOpen(software, vendorName))
                        {
                            if (vendor == null)
                            {
                                continue;
                            }

                            foreach (var titleName in Names(vendor))
                            {
                                var titleKey = LudusaviManifest.NormalizeName(titleName);
                                if (titleKey.Length >= 3 && wanted.Any(w => w == titleKey))
                                {
                                    results.Add("HKEY_CURRENT_USER\\Software\\" + vendorName + "\\" + titleName);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: registry guessing failed.");
            }

            return results;
        }

        private static IEnumerable<string> Names(RegistryKey key)
        {
            try
            {
                return key.GetSubKeyNames();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private static RegistryKey SafeOpen(RegistryKey parent, string name)
        {
            try
            {
                return parent.OpenSubKey(name, false);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
