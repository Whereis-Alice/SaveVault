using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace SaveVault.Services
{
    /// <summary>One manifest title reduced to the parts a backup tool needs.</summary>
    public class LudusaviEntry
    {
        [SerializationPropertyName("t")]
        public string Title { get; set; }

        /// <summary>Raw, still tokenised file paths tagged as saves.</summary>
        [SerializationPropertyName("f")]
        public List<string> Files { get; set; } = new List<string>();

        /// <summary>Raw registry keys tagged as saves.</summary>
        [SerializationPropertyName("r")]
        public List<string> Registry { get; set; } = new List<string>();

        /// <summary>Known install folder names, used to match a local folder to this title.</summary>
        [SerializationPropertyName("d")]
        public List<string> InstallDirs { get; set; } = new List<string>();
    }

    /// <summary>Compact on-disk cache so the 17 MB manifest is only parsed when it changes.</summary>
    public class LudusaviCache
    {
        public const int CurrentSchema = 1;

        [SerializationPropertyName("schema")]
        public int Schema { get; set; } = CurrentSchema;

        [SerializationPropertyName("source")]
        public string Source { get; set; }

        [SerializationPropertyName("size")]
        public long Size { get; set; }

        [SerializationPropertyName("mtime")]
        public DateTime Mtime { get; set; }

        [SerializationPropertyName("entries")]
        public List<LudusaviEntry> Entries { get; set; } = new List<LudusaviEntry>();
    }

    /// <summary>
    /// Read only reuse of the manifest that the Ludusavi extension already downloads. The
    /// file is a 17 MB, 875 000 line YAML document with roughly 53 000 titles, so it is
    /// parsed with a hand written line scanner rather than a YAML library, reduced to the
    /// 13 000 titles that actually declare save data, and cached as JSON.
    ///
    /// Only paths tagged "save" are kept. Every path in the manifest carries explicit tags,
    /// so nothing is lost by ignoring the untagged case, and config-only paths are dropped
    /// deliberately: they are settings, not progress.
    /// </summary>
    public class LudusaviManifest
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly Dictionary<string, LudusaviEntry> byTitle = new Dictionary<string, LudusaviEntry>();
        private readonly Dictionary<string, LudusaviEntry> byInstallDir = new Dictionary<string, LudusaviEntry>();

        public int EntryCount { get; private set; }
        public string SourcePath { get; private set; }
        public bool Loaded { get; private set; }

        /// <summary>Standard location used by the Ludusavi desktop application.</summary>
        public static string DefaultManifestPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "ludusavi", "manifest.yaml");
        }

        /// <summary>
        /// Loads the manifest, preferring the JSON cache when it still matches the source
        /// file. Failures are logged and swallowed: this layer is an optional bonus.
        /// </summary>
        public void Load(string manifestPath, string cachePath)
        {
            Loaded = false;
            byTitle.Clear();
            byInstallDir.Clear();
            EntryCount = 0;

            var path = string.IsNullOrWhiteSpace(manifestPath) ? DefaultManifestPath() : manifestPath.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            SourcePath = path;
            var info = new FileInfo(path);

            var cached = ReadCache(cachePath, path, info);
            if (cached != null)
            {
                Index(cached.Entries);
                Loaded = true;
                logger.Info("Save Vault: reused Ludusavi cache with " + EntryCount + " titles.");
                return;
            }

            List<LudusaviEntry> entries;
            try
            {
                entries = Parse(path);
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: failed to parse the Ludusavi manifest.");
                return;
            }

            Index(entries);
            Loaded = true;
            WriteCache(cachePath, path, info, entries);
            logger.Info("Save Vault: parsed the Ludusavi manifest, kept " + EntryCount + " titles with save data.");
        }

        private LudusaviCache ReadCache(string cachePath, string sourcePath, FileInfo info)
        {
            if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
            {
                return null;
            }

            try
            {
                var cache = Serialization.FromJson<LudusaviCache>(File.ReadAllText(cachePath));
                if (cache == null || cache.Schema != LudusaviCache.CurrentSchema || cache.Entries == null)
                {
                    return null;
                }

                if (!string.Equals(cache.Source, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (cache.Size != info.Length || (cache.Mtime - info.LastWriteTimeUtc).Duration() > TimeSpan.FromSeconds(2))
                {
                    return null;
                }

                return cache;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: ignoring an unreadable Ludusavi cache.");
                return null;
            }
        }

        private void WriteCache(string cachePath, string sourcePath, FileInfo info, List<LudusaviEntry> entries)
        {
            if (string.IsNullOrEmpty(cachePath))
            {
                return;
            }

            try
            {
                var folder = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var cache = new LudusaviCache
                {
                    Source = sourcePath,
                    Size = info.Length,
                    Mtime = info.LastWriteTimeUtc,
                    Entries = entries
                };

                File.WriteAllText(cachePath, Serialization.ToJson(cache, false), Encoding.UTF8);
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: could not write the Ludusavi cache.");
            }
        }

        /// <summary>
        /// Line scanner for the manifest. Top level keys are titles at indent 0, the sections
        /// we care about sit at indent 2, path keys at indent 4 and their tags below that.
        /// </summary>
        private static List<LudusaviEntry> Parse(string path)
        {
            var result = new List<LudusaviEntry>();

            LudusaviEntry current = null;
            var section = Section.None;
            string pendingPath = null;
            var pendingTags = new List<string>();

            Action flushPath = () =>
            {
                if (current != null && pendingPath != null && pendingTags.Contains("save"))
                {
                    if (section == Section.Files)
                    {
                        current.Files.Add(pendingPath);
                    }
                    else if (section == Section.Registry)
                    {
                        current.Registry.Add(pendingPath);
                    }
                }

                pendingPath = null;
                pendingTags.Clear();
            };

            Action flushEntry = () =>
            {
                flushPath();
                if (current != null && (current.Files.Count > 0 || current.Registry.Count > 0))
                {
                    result.Add(current);
                }

                current = null;
            };

            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed == "---" || trimmed[0] == '#')
                {
                    continue;
                }

                var indent = raw.Length - raw.TrimStart(' ').Length;

                if (indent == 0)
                {
                    flushEntry();
                    current = new LudusaviEntry { Title = Unquote(raw) };
                    section = Section.None;
                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                if (indent == 2)
                {
                    flushPath();
                    var key = Unquote(raw);
                    if (key == "files")
                    {
                        section = Section.Files;
                    }
                    else if (key == "registry")
                    {
                        section = Section.Registry;
                    }
                    else if (key == "installDir")
                    {
                        section = Section.InstallDir;
                    }
                    else
                    {
                        section = Section.None;
                    }

                    continue;
                }

                if (section == Section.None)
                {
                    continue;
                }

                if (indent == 4)
                {
                    flushPath();
                    if (section == Section.InstallDir)
                    {
                        var dir = Unquote(raw.Replace(": {}", ":"));
                        if (!string.IsNullOrEmpty(dir))
                        {
                            current.InstallDirs.Add(dir);
                        }
                    }
                    else
                    {
                        pendingPath = Unquote(raw);
                    }

                    continue;
                }

                if (pendingPath != null && trimmed.Length > 2 && trimmed[0] == '-' && trimmed[1] == ' ')
                {
                    pendingTags.Add(trimmed.Substring(2).Trim());
                }
            }

            flushEntry();
            return result;
        }

        private enum Section
        {
            None,
            Files,
            Registry,
            InstallDir
        }

        /// <summary>Strips the trailing colon and the optional YAML quoting from a mapping key.</summary>
        private static string Unquote(string line)
        {
            var text = line.Trim();
            if (text.EndsWith(":", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 1).Trim();
            }

            if (text.Length >= 2)
            {
                var first = text[0];
                var last = text[text.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                {
                    text = text.Substring(1, text.Length - 2);
                    if (first == '"')
                    {
                        text = text.Replace("\\\"", "\"").Replace("\\\\", "\\");
                    }
                }
            }

            return text;
        }

        private void Index(List<LudusaviEntry> entries)
        {
            foreach (var entry in entries)
            {
                var key = NormalizeName(entry.Title);
                if (key.Length > 0 && !byTitle.ContainsKey(key))
                {
                    byTitle[key] = entry;
                }

                foreach (var dir in entry.InstallDirs)
                {
                    var dirKey = NormalizeName(dir);
                    if (dirKey.Length > 0 && !byInstallDir.ContainsKey(dirKey))
                    {
                        byInstallDir[dirKey] = entry;
                    }
                }
            }

            EntryCount = entries.Count;
        }

        /// <summary>
        /// Collapses a title to something comparable: lower case, no punctuation, no spaces.
        /// CJK characters are kept, so Japanese titles still match each other.
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name.Length);
            foreach (var c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        /// <summary>First entry matching any of the supplied names or folder names.</summary>
        public LudusaviEntry Find(IEnumerable<string> names, IEnumerable<string> folderNames)
        {
            if (!Loaded)
            {
                return null;
            }

            if (names != null)
            {
                foreach (var name in names)
                {
                    var key = NormalizeName(name);
                    LudusaviEntry hit;
                    if (key.Length > 0 && byTitle.TryGetValue(key, out hit))
                    {
                        return hit;
                    }
                }
            }

            if (folderNames != null)
            {
                foreach (var folder in folderNames)
                {
                    var key = NormalizeName(folder);
                    LudusaviEntry hit;
                    if (key.Length > 0 && byInstallDir.TryGetValue(key, out hit))
                    {
                        return hit;
                    }
                }
            }

            return null;
        }
    }
}
