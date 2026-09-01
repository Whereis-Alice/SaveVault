using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Models;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>Folder and file names that are never worth walking into.</summary>
    public static class ScanNoise
    {
        private static readonly HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "microsoftedge", "google", "mozilla", "packages", "temp", "tmp", "cache", "caches",
            "crashdumps", "crashreports", "d3dscache", "nvidia", "amd", "intel", "playnite", "connecteddevicesplatform",
            "comms", "iconcache", "fontcache", "webcache", "gpucache", "shadercache", "assemblies", "clr_v4.0",
            "diagnostics", "elevatedDiagnostics", "history", "inetcache", "internetcache", "publisher",
            "windows", "windowsapps", "windows nt", "installer", "spotify", "discord", "steam", "epic games",
            "electron", "node_modules", ".git", "$recycle.bin", "system volume information", "onedrive",
            "logs", "log", "dumps", "minidumps", "telemetry", "eventcache", "sentry", "vulkan", "directx",
            "assets", "asset", "build", "builds", "dist", "obj", "src", "venv", "__pycache__",
            "backup", "backups", "downloads", "screenshots", "crash", "crashes"
        };

        /// <summary>
        /// True for folders that are never a save location. Besides the known names, anything
        /// starting with a dot, an underscore, a dollar or a percent sign is skipped: those are
        /// scratch, tooling and version control folders. Matching them by name produced false
        /// hits such as a "_tmp_&lt;title&gt;_assets" scratch folder being taken for the save
        /// folder of that title.
        /// </summary>
        public static bool IsNoise(string folderName)
        {
            if (string.IsNullOrEmpty(folderName))
            {
                return true;
            }

            var name = folderName.Trim();
            if (name.Length == 0)
            {
                return true;
            }

            var first = name[0];
            if (first == '.' || first == '_' || first == '%' || first == '$')
            {
                return true;
            }

            return folders.Contains(name);
        }
    }

    /// <summary>
    /// Finds where a game keeps its saves. Six layers are consulted, and the origin of each
    /// hit records how much it can be trusted.
    ///
    /// The reason this plugin exists is that a database lookup is not enough. The Ludusavi
    /// manifest knows about 13 000 titles with save data, which covers Steam well and covers
    /// a Japanese visual novel library barely at all, so the heuristic layers and the runtime
    /// observation carry most of the weight here.
    /// </summary>
    public class SaveScanner
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Directory names that mean "saves" across the engines this library uses.</summary>
        private static readonly HashSet<string> saveFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "save", "saves", "savedata", "save_data", "savedatas", "savefile", "savefiles", "savegame",
            "savegames", "sav", "savs", "sdata", "userdata", "user data", "usersave", "profile", "profiles",
            "players", "player", "slot", "slots",
            "セーブ", "セーブデータ", "せーぶ", "データ", "存档", "存檔", "저장"
        };

        /// <summary>
        /// Patterns for engines that drop numbered save files straight next to the executable.
        /// NScripter and its descendants do this, which is why the install folder is scanned
        /// with a filter instead of being copied whole.
        /// </summary>
        private static readonly string[] looseSavePatterns =
        {
            "save*.dat", "sav*.dat", "*.sav", "*.svd", "*.qsv", "*.gsv", "*.ksd", "*.asd", "envdata", "global.dat"
        };

        /// <summary>True for folder names that mean "saves" in one of the supported engines.</summary>
        public static bool IsSaveFolderName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && saveFolderNames.Contains(name.Trim());
        }

        private static readonly Regex decoration = new Regex(@"[\[\(【（][^\]\)】）]*[\]\)】）]", RegexOptions.Compiled);

        private readonly SaveVaultSettings settings;
        private readonly LudusaviManifest manifest;

        public SaveScanner(SaveVaultSettings settings, LudusaviManifest manifest)
        {
            this.settings = settings;
            this.manifest = manifest;
        }

        /// <summary>
        /// Runs every enabled layer and returns the merged candidate list, best first. The
        /// caller decides what to do with it; nothing is written here.
        /// </summary>
        public List<SaveTarget> Scan(Game game)
        {
            var found = new List<SaveTarget>();
            if (game == null)
            {
                return found;
            }

            var installDir = SafeInstallDir(game);
            var names = NameTokens(game).ToList();

            if (settings.UseLudusaviManifest)
            {
                AddAll(found, FromManifest(game, installDir, names));
            }

            if (settings.ScanInstallDir && installDir != null)
            {
                AddAll(found, FromInstallDir(installDir));

                var mirror = VirtualStoreMirror(installDir);
                if (mirror != null)
                {
                    AddAll(found, FromInstallDir(mirror));
                }
            }

            if (settings.ScanUserFolders)
            {
                AddAll(found, FromUserFolders(names));
            }

            if (settings.IncludeRegistry)
            {
                AddAll(found, FromRegistry(game, names));
            }

            return found
                .OrderBy(t => (int)t.Origin)
                .ThenByDescending(t => t.Confidence)
                .ToList();
        }

        /// <summary>Install directory as an absolute path, or null when the game is not installed.</summary>
        public static string SafeInstallDir(Game game)
        {
            try
            {
                var dir = game.InstallDirectory;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return null;
                }

                dir = dir.Trim().TrimEnd('\\', '/');
                return Directory.Exists(dir) ? dir : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Every string worth matching against: the display name, the sorting name, the
        /// original name, the install folder name, and all of those with the bracketed
        /// release-group decorations that this library uses stripped off.
        /// </summary>
        public static IEnumerable<string> NameTokens(Game game)
        {
            var raw = new List<string>();

            if (game != null)
            {
                raw.Add(game.Name);
                raw.Add(game.SortingName);

                var installDir = SafeInstallDir(game);
                if (installDir != null)
                {
                    raw.Add(new DirectoryInfo(installDir).Name);
                }
            }

            var result = new List<string>();
            foreach (var item in raw)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                var text = item.Trim();
                Push(result, text);

                var stripped = decoration.Replace(text, " ").Trim();
                Push(result, stripped);

                var cut = stripped.Split('~')[0].Trim();
                Push(result, cut);
            }

            return result;
        }

        /// <summary>Developer and publisher names, used only by the registry guess.</summary>
        private static IEnumerable<string> VendorTokens(Game game)
        {
            var result = new List<string>();
            if (game == null)
            {
                return result;
            }

            if (game.Developers != null)
            {
                foreach (var item in game.Developers)
                {
                    Push(result, item.Name);
                }
            }

            if (game.Publishers != null)
            {
                foreach (var item in game.Publishers)
                {
                    Push(result, item.Name);
                }
            }

            return result;
        }

        private static void Push(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var text = value.Trim();
            if (text.Length < 2)
            {
                return;
            }

            if (!list.Any(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(text);
            }
        }

        private static void AddAll(List<SaveTarget> into, IEnumerable<SaveTarget> items)
        {
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                var existing = into.FirstOrDefault(t => t.SameAs(item));
                if (existing == null)
                {
                    into.Add(item);
                    continue;
                }

                if ((int)item.Origin < (int)existing.Origin)
                {
                    existing.Origin = item.Origin;
                }

                existing.Confidence = Math.Max(existing.Confidence, item.Confidence);
                if (string.IsNullOrEmpty(existing.Filter))
                {
                    existing.Filter = item.Filter;
                }
            }
        }

        // ---------------------------------------------------------------- layer 2: manifest

        private IEnumerable<SaveTarget> FromManifest(Game game, string installDir, List<string> names)
        {
            var result = new List<SaveTarget>();
            if (manifest == null || !manifest.Loaded)
            {
                return result;
            }

            var folders = new List<string>();
            if (installDir != null)
            {
                folders.Add(new DirectoryInfo(installDir).Name);
            }

            var entry = manifest.Find(names, folders);
            if (entry == null)
            {
                return result;
            }

            foreach (var raw in entry.Files)
            {
                var expanded = PathTokens.Expand(raw, installDir);
                if (expanded == null)
                {
                    continue;
                }

                string root;
                string pattern;
                PathTokens.SplitWildcard(expanded, out root, out pattern);

                foreach (var resolved in ResolveWildcard(root, pattern))
                {
                    result.Add(new SaveTarget
                    {
                        Kind = TargetKind.Folder,
                        Path = resolved.Item1,
                        Filter = resolved.Item2,
                        Origin = TargetOrigin.Ludusavi,
                        Confidence = 90,
                        Note = entry.Title
                    });
                }
            }

            if (settings.IncludeRegistry)
            {
                foreach (var raw in entry.Registry)
                {
                    var key = RegistryBridge.NormalizeKey(raw);
                    if (key == null || !RegistryBridge.Exists(key))
                    {
                        continue;
                    }

                    result.Add(new SaveTarget
                    {
                        Kind = TargetKind.Registry,
                        Path = key,
                        Origin = TargetOrigin.Ludusavi,
                        Confidence = 85,
                        Note = entry.Title
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Turns a manifest path into concrete folders. A trailing file pattern becomes the
        /// target filter so the parent folder is never copied whole, and a wildcard in the
        /// middle of the path is expanded against the file system.
        /// </summary>
        private static IEnumerable<Tuple<string, string>> ResolveWildcard(string root, string pattern)
        {
            var result = new List<Tuple<string, string>>();

            if (!PathTokens.DirectoryUsable(root))
            {
                // The parent may itself be the pattern holder, for example <base>/save000*.dta.
                if (string.IsNullOrEmpty(pattern) || !PathTokens.DirectoryUsable(Path.GetDirectoryName(root ?? string.Empty)))
                {
                    return result;
                }
            }

            if (string.IsNullOrEmpty(pattern))
            {
                if (PathTokens.DirectoryUsable(root))
                {
                    result.Add(Tuple.Create(root, (string)null));
                }
                else
                {
                    // A concrete file: back up its parent, filtered to that single name.
                    var parent = Path.GetDirectoryName(root);
                    var leaf = Path.GetFileName(root);
                    if (PathTokens.DirectoryUsable(parent) && !string.IsNullOrEmpty(leaf))
                    {
                        result.Add(Tuple.Create(parent, leaf));
                    }
                }

                return result;
            }

            var segments = pattern.Split('\\');
            if (segments.Length == 1)
            {
                // Single trailing pattern: keep the folder and use the pattern as a filter.
                if (PathTokens.DirectoryUsable(root))
                {
                    result.Add(Tuple.Create(root, segments[0]));
                }

                return result;
            }

            // A wildcard folder in the middle. Expand one level and recurse on the rest.
            var head = segments[0];
            var tail = string.Join("\\", segments.Skip(1));

            try
            {
                foreach (var child in Directory.GetDirectories(root, head == "**" ? "*" : head))
                {
                    foreach (var nested in ResolveWildcard(child, tail))
                    {
                        result.Add(nested);
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable folder, nothing to contribute.
            }

            return result;
        }

        // ------------------------------------------------------------ layer 3: install dir

        private IEnumerable<SaveTarget> FromInstallDir(string installDir)
        {
            var result = new List<SaveTarget>();

            foreach (var folder in Descend(installDir, 3))
            {
                var name = new DirectoryInfo(folder).Name;
                if (!saveFolderNames.Contains(name))
                {
                    continue;
                }

                if (!WithinCaps(folder, null))
                {
                    continue;
                }

                result.Add(new SaveTarget
                {
                    Kind = TargetKind.Folder,
                    Path = folder,
                    Origin = TargetOrigin.InstallFolder,
                    Confidence = 75,
                    Note = name
                });
            }

            var loose = LoosePatterns(installDir);
            if (loose != null)
            {
                result.Add(new SaveTarget
                {
                    Kind = TargetKind.Folder,
                    Path = installDir,
                    Filter = loose,
                    Origin = TargetOrigin.InstallFolder,
                    Confidence = 65,
                    Note = "loose"
                });
            }

            return result;
        }

        /// <summary>
        /// Save files sitting directly in the game folder. Returns the semicolon separated
        /// patterns that actually matched, or null. Without this the only options would be
        /// missing the saves or archiving the entire game.
        /// </summary>
        private static string LoosePatterns(string folder)
        {
            var hits = new List<string>();

            foreach (var pattern in looseSavePatterns)
            {
                try
                {
                    var files = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0 && files.Length <= 200)
                    {
                        hits.Add(pattern);
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return hits.Count == 0 ? null : string.Join(";", hits);
        }

        /// <summary>Breadth first walk of a folder tree, noise folders excluded.</summary>
        private static IEnumerable<string> Descend(string root, int maxDepth)
        {
            var result = new List<string>();
            if (!PathTokens.DirectoryUsable(root))
            {
                return result;
            }

            var current = new List<string> { root };
            for (var depth = 0; depth < maxDepth; depth++)
            {
                var next = new List<string>();
                foreach (var folder in current)
                {
                    string[] children;
                    try
                    {
                        children = Directory.GetDirectories(folder);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (var child in children)
                    {
                        var name = new DirectoryInfo(child).Name;
                        if (ScanNoise.IsNoise(name))
                        {
                            continue;
                        }

                        result.Add(child);
                        next.Add(child);
                    }
                }

                if (next.Count == 0)
                {
                    break;
                }

                current = next;
            }

            return result;
        }

        // ----------------------------------------------------------- layer 4: user folders

        private IEnumerable<SaveTarget> FromUserFolders(List<string> names)
        {
            var result = new List<SaveTarget>();
            var wanted = names.Select(LudusaviManifest.NormalizeName).Where(n => n.Length >= 3).Distinct().ToList();
            if (wanted.Count == 0)
            {
                return result;
            }

            var prefixes = WordPrefixKeys(names);

            foreach (var root in PathTokens.UserRoots())
            {
                if (!PathTokens.DirectoryUsable(root))
                {
                    continue;
                }

                foreach (var level1 in SafeDirectories(root))
                {
                    var name1 = new DirectoryInfo(level1).Name;
                    if (ScanNoise.IsNoise(name1))
                    {
                        continue;
                    }

                    if (Matches(wanted, prefixes, name1))
                    {
                        if (WithinCaps(level1, null))
                        {
                            result.Add(new SaveTarget
                            {
                                Kind = TargetKind.Folder,
                                Path = level1,
                                Origin = TargetOrigin.UserFolder,
                                Confidence = 70,
                                Note = name1
                            });
                        }

                        continue;
                    }

                    // Second level catches the vendor\title layout used by most engines.
                    foreach (var level2 in SafeDirectories(level1))
                    {
                        var name2 = new DirectoryInfo(level2).Name;
                        if (ScanNoise.IsNoise(name2))
                        {
                            continue;
                        }

                        if (!Matches(wanted, prefixes, name2))
                        {
                            continue;
                        }

                        if (WithinCaps(level2, null))
                        {
                            result.Add(new SaveTarget
                            {
                                Kind = TargetKind.Folder,
                                Path = level2,
                                Origin = TargetOrigin.UserFolder,
                                Confidence = 60,
                                Note = name1 + "\\" + name2
                            });
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Every shortened form of a title that a folder is allowed to be named after: the
        /// title cut at a word boundary. "ARK Survival Evolved" yields "ark", "arksurvival"
        /// and "arksurvivalevolved", so a stray folder called "arks" no longer counts as a
        /// match while "eden" still matches "eden* They were only two, on the planet.".
        /// </summary>
        private static HashSet<string> WordPrefixKeys(IEnumerable<string> names)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (names == null)
            {
                return result;
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var builder = new StringBuilder(name.Length);
                var pending = false;
                foreach (var c in name.ToLowerInvariant())
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        builder.Append(c);
                        pending = true;
                        continue;
                    }

                    if (pending && builder.Length >= 4)
                    {
                        result.Add(builder.ToString());
                    }

                    pending = false;
                }

                if (builder.Length >= 4)
                {
                    result.Add(builder.ToString());
                }
            }

            return result;
        }

        /// <summary>
        /// Name comparison for folder matching. Equality is ideal. A folder may also be a
        /// shortened title, but only when the cut lands on a word boundary, or the title plus
        /// a short suffix such as "SaveData".
        /// </summary>
        private static bool Matches(List<string> wanted, HashSet<string> prefixes, string candidate)
        {
            var key = LudusaviManifest.NormalizeName(candidate);
            if (key.Length < 3)
            {
                return false;
            }

            if (key.Length >= 4 && prefixes.Contains(key))
            {
                return true;
            }

            foreach (var want in wanted)
            {
                if (want == key)
                {
                    return true;
                }

                if (key.Length < 5 || want.Length < 5)
                {
                    continue;
                }

                // The folder name starts with the title and adds a little text, as in
                // "<title> SaveData" or "BALDRHEARTEXE".
                if (key.StartsWith(want, StringComparison.Ordinal) && key.Length - want.Length <= 12)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Mirror of an install folder under the UAC virtual store. Legacy installers that
        /// write saves into Program Files end up here without the game ever knowing.
        /// </summary>
        public static string VirtualStoreMirror(string installDir)
        {
            var store = PathTokens.VirtualStore();
            if (string.IsNullOrEmpty(store) || string.IsNullOrEmpty(installDir) || installDir.Length < 4)
            {
                return null;
            }

            if (installDir[1] != ':')
            {
                return null;
            }

            var tail = installDir.Substring(3);
            var candidate = Path.Combine(store, tail);
            return PathTokens.DirectoryUsable(candidate) ? candidate : null;
        }

        // -------------------------------------------------------------- layer 5: registry

        private IEnumerable<SaveTarget> FromRegistry(Game game, List<string> names)
        {
            var tokens = names.Concat(VendorTokens(game));
            return RegistryBridge.Guess(tokens).Select(key => new SaveTarget
            {
                Kind = TargetKind.Registry,
                Path = key,
                Origin = TargetOrigin.RegistryGuess,
                Confidence = 40,
                Enabled = false,
                Note = "guess"
            });
        }

        // ------------------------------------------------------------------------ helpers

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return Directory.GetDirectories(folder);
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <summary>
        /// Rejects candidates that are too large or too numerous to be saves. This is the
        /// guard that stops a wrong guess from filling the vault with game assets.
        /// </summary>
        private bool WithinCaps(string folder, string filter)
        {
            long bytes = 0;
            var files = 0;
            var maxBytes = (long)Math.Max(1, settings.MaxCandidateMegabytes) * 1024L * 1024L;
            var maxFiles = Math.Max(10, settings.MaxCandidateFiles);

            try
            {
                foreach (var file in FileWalker.Enumerate(folder, filter))
                {
                    files++;
                    bytes += file.Length;

                    if (files > maxFiles || bytes > maxBytes)
                    {
                        logger.Debug("Save Vault: skipping oversized candidate " + folder);
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return files > 0;
        }

        /// <summary>
        /// Applies newly detected targets to a profile without losing user intent: manual
        /// entries and the enabled flag are never overwritten, and a stronger origin only
        /// upgrades an existing entry.
        /// </summary>
        public static int Merge(GameSaveProfile profile, IEnumerable<SaveTarget> found)
        {
            var added = 0;

            foreach (var target in found)
            {
                var existing = profile.Targets.FirstOrDefault(t => t.SameAs(target));
                if (existing == null)
                {
                    profile.Targets.Add(target.Clone());
                    added++;
                    continue;
                }

                if (existing.Origin == TargetOrigin.Manual)
                {
                    continue;
                }

                if ((int)target.Origin < (int)existing.Origin)
                {
                    existing.Origin = target.Origin;
                }

                existing.Confidence = Math.Max(existing.Confidence, target.Confidence);

                if (string.IsNullOrEmpty(existing.Filter) && !string.IsNullOrEmpty(target.Filter))
                {
                    existing.Filter = target.Filter;
                }
            }

            profile.Targets = profile.Targets
                .OrderBy(t => (int)t.Origin)
                .ThenByDescending(t => t.Confidence)
                .ToList();

            return added;
        }
    }
}
