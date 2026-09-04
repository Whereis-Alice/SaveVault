using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>Everything the guard needs to judge one game's observed folders.</summary>
    public class LearnContext
    {
        public string InstallDir { get; set; }
        public string Mirror { get; set; }
        public string VaultRoot { get; set; }
        public SaveScanner.NameMatcher Matcher { get; set; }
        public int MaxMegabytes { get; set; }
        public int MaxFiles { get; set; }

        public static LearnContext For(Game game, SaveVaultSettings settings)
        {
            var installDir = SaveScanner.SafeInstallDir(game);

            return new LearnContext
            {
                InstallDir = installDir,
                Mirror = installDir == null ? null : SaveScanner.VirtualStoreMirror(installDir),
                VaultRoot = settings == null ? null : settings.EffectiveBackupRoot,
                Matcher = SaveScanner.MatcherFor(game),
                MaxMegabytes = settings == null ? 256 : settings.MaxCandidateMegabytes,
                MaxFiles = settings == null ? 5000 : settings.MaxCandidateFiles
            };
        }
    }

    /// <summary>
    /// Decides whether a folder that changed while a game was running may be recorded as a
    /// save location.
    ///
    /// Runtime observation on its own is naive. A before and after diff of the user folders
    /// records everything that wrote to disk during the session, and on a normal desktop that
    /// is a messenger, a driver panel, a couple of Electron shells and a browser cache. Version
    /// 1.0.0 did exactly that: one visual novel came out of a single session with seven save
    /// locations, six of which belonged to other programs, and the resulting snapshot was
    /// 446 MB of other people's caches.
    ///
    /// A folder now has to prove it belongs to the game before it is believed:
    /// <list type="bullet">
    ///   <item>it sits under the game's install folder or its UAC mirror, or</item>
    ///   <item>a folder in its path is named after the title, or</item>
    ///   <item>a folder in its path literally means "saves" in one of the supported engines.</item>
    /// </list>
    /// Whichever route it takes, it must also stay inside the candidate size caps, and if it
    /// took none of them it is dropped. The blocklist of application folders is the last line
    /// rather than the first: a game may legitimately be named after something on it.
    /// </summary>
    public static class LearnedGuard
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>
        /// Bumped whenever these rules change. The plugin re-checks stored learned locations
        /// once per version, which is how the bad entries written by 1.0.0 disappear without
        /// the user having to find them.
        /// </summary>
        public const int Version = 1;

        /// <summary>
        /// Extensions a save file plausibly has. Deliberately broad: this is only ever an
        /// additional condition for a folder that already belongs to the game, never a reason
        /// to accept one on its own.
        /// </summary>
        private static readonly HashSet<string> saveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sav", ".save", ".sv", ".svd", ".svs", ".sdat", ".dat", ".bin", ".qsv", ".gsv", ".dsv",
            ".ksd", ".asd", ".lsd", ".rvdata", ".rvdata2", ".rxdata", ".ess", ".fos", ".es3", ".sgm",
            ".slot", ".mem", ".prof", ".usr", ".user", ".sol", ".json", ".xml", ".ini", ".cfg",
            ".conf", ".config", ".db", ".sqlite", ".sqlite3", ".yml", ".yaml", ".txt", ".csv", ".plist"
        };

        /// <summary>Extensionless file names that engines use for global save data.</summary>
        private static readonly HashSet<string> saveFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "envdata", "global", "globaldata", "gamedata", "savedata", "system"
        };

        private static readonly object rootGate = new object();
        private static List<string> knownRoots;

        /// <summary>
        /// True when the folder may be recorded for this game. The reason is English and goes
        /// to the log only; nothing user facing depends on it.
        /// </summary>
        public static bool Plausible(string path, LearnContext context, out string reason)
        {
            bool convincing;
            return Judge(path, context, out convincing, out reason);
        }

        /// <summary>
        /// Same judgement plus the extra condition that only the live watcher can check: a
        /// folder that is neither named after the title nor named like a save folder has to
        /// contain at least one file that could be a save. That rules out the log and cache
        /// folders a game writes inside its own install directory.
        /// </summary>
        public static bool PlausibleObserved(string path, LearnContext context, IEnumerable<string> changedFiles, out string reason)
        {
            bool convincing;
            if (!Judge(path, context, out convincing, out reason))
            {
                return false;
            }

            if (convincing)
            {
                return true;
            }

            if (changedFiles != null && changedFiles.Any(LooksLikeSaveFile))
            {
                return true;
            }

            reason = "no file that looks like a save";
            return false;
        }

        /// <summary>
        /// The actual rules. <paramref name="convincing" /> reports whether the folder earned
        /// its place by name - after the title or after saves - rather than merely by sitting
        /// under the install directory.
        /// </summary>
        private static bool Judge(string path, LearnContext context, out bool convincing, out string reason)
        {
            convincing = false;
            reason = null;

            if (string.IsNullOrWhiteSpace(path) || context == null)
            {
                reason = "empty";
                return false;
            }

            var full = path.Trim().TrimEnd('\\');
            if (full.Length < 4)
            {
                reason = "too short";
                return false;
            }

            // Backing the vault up into itself, or capturing Playnite's own configuration, is
            // never right, and both change constantly while a game runs.
            if (Under(full, context.VaultRoot))
            {
                reason = "inside the vault";
                return false;
            }

            if (Under(full, PlayniteConfigFolder()))
            {
                reason = "playnite configuration";
                return false;
            }

            var rootSegments = Segments(full);
            if (rootSegments.Count == 0)
            {
                reason = "no folder below a known root";
                return false;
            }

            var installBase = Under(full, context.InstallDir) ? context.InstallDir
                : Under(full, context.Mirror) ? context.Mirror : null;

            // Below the install directory the surrounding path belongs to the launcher, not to
            // the game: "steamapps\common" would trip the blocklist for no reason. Only the part
            // the game itself created is judged.
            var localSegments = installBase == null ? rootSegments : Below(full, installBase);
            if (localSegments.Count == 0)
            {
                reason = "the install directory itself";
                return false;
            }

            var named = localSegments.Any(SaveScanner.IsSaveFolderName);
            var matched = context.Matcher != null && rootSegments.Any(context.Matcher.Matches);
            convincing = named || matched;

            if (installBase == null && !convincing)
            {
                reason = "unrelated to the game";
                return false;
            }

            // The blocklist comes last rather than first, because a game may legitimately be
            // named after something on it; a title match therefore wins. A folder that means
            // "saves" is not itself an application folder, so those segments are exempt.
            if (!matched)
            {
                var application = localSegments
                    .FirstOrDefault(part => ScanNoise.IsApplication(part) && !SaveScanner.IsSaveFolderName(part));

                if (application != null)
                {
                    reason = "application folder " + application;
                    return false;
                }
            }

            var exists = false;
            try
            {
                exists = Directory.Exists(full);
            }
            catch (Exception)
            {
                exists = false;
            }

            if (!exists)
            {
                reason = "folder no longer exists";
                return false;
            }

            if (!SaveScanner.WithinCaps(full, null, context.MaxMegabytes, context.MaxFiles))
            {
                reason = "empty or over the size cap";
                return false;
            }

            return true;
        }

        /// <summary>True for a file whose name suggests save data rather than a cache or a log.</summary>
        public static bool LooksLikeSaveFile(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return false;
            }

            string extension;
            string name;

            try
            {
                extension = Path.GetExtension(file);
                name = Path.GetFileNameWithoutExtension(file);
            }
            catch (Exception)
            {
                return false;
            }

            if (string.IsNullOrEmpty(extension))
            {
                return name != null && saveFileNames.Contains(name);
            }

            return saveExtensions.Contains(extension);
        }

        /// <summary>Path equality and containment, the only two forms used here.</summary>
        public static bool Under(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }

            var trimmed = root.TrimEnd('\\');
            if (trimmed.Length == 0)
            {
                return false;
            }

            return string.Equals(path, trimmed, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Folder names below the deepest known root, or below the volume when the path is
        /// somewhere else entirely. The point is that a drive letter, a user name or "AppData"
        /// must never be offered to the title matcher as a candidate name.
        /// </summary>
        public static List<string> Segments(string path)
        {
            var full = path.TrimEnd('\\');
            string best = null;

            foreach (var root in Roots())
            {
                if (Under(full, root) && (best == null || root.Length > best.Length))
                {
                    best = root;
                }
            }

            string relative;
            if (best != null)
            {
                var trimmed = best.TrimEnd('\\');
                relative = full.Length > trimmed.Length ? full.Substring(trimmed.Length + 1) : string.Empty;
            }
            else
            {
                string volume;
                try
                {
                    volume = Path.GetPathRoot(full) ?? string.Empty;
                }
                catch (Exception)
                {
                    volume = string.Empty;
                }

                relative = full.Length > volume.Length ? full.Substring(volume.Length) : string.Empty;
            }

            return relative
                .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        /// <summary>Folder names below a given base directory.</summary>
        public static List<string> Below(string path, string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir))
            {
                return new List<string>();
            }

            var full = path.TrimEnd('\\');
            var trimmed = baseDir.TrimEnd('\\');
            if (full.Length <= trimmed.Length)
            {
                return new List<string>();
            }

            return full
                .Substring(trimmed.Length + 1)
                .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        /// <summary>Playnite's own configuration folder, which the watcher sees change constantly.</summary>
        public static string PlayniteConfigFolder()
        {
            try
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "Playnite");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IEnumerable<string> Roots()
        {
            lock (rootGate)
            {
                if (knownRoots == null)
                {
                    var roots = new List<string>();

                    try
                    {
                        roots.AddRange(PathTokens.UserRoots());
                        roots.AddRange(PathTokens.LearningRoots());
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "Save Vault: could not enumerate the known roots.");
                    }

                    knownRoots = roots
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Select(r => r.TrimEnd('\\'))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return knownRoots;
            }
        }
    }
}
