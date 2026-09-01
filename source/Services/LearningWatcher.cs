using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>
    /// Learns save locations by watching what a game actually writes.
    ///
    /// A fingerprint of the user folders is taken when the game starts and compared when it
    /// stops; anything that changed in between is a save location by definition. This is the
    /// only layer that works for titles no database has ever heard of, which in a Japanese
    /// visual novel library is most of them.
    ///
    /// The walk runs on a background thread with hard caps on depth and file count, and it
    /// only stores a path, a size and a timestamp per file, so it stays out of the way of the
    /// launching game.
    /// </summary>
    public class LearningWatcher
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private const int MaxDepth = 5;
        private const int MaxFiles = 60000;
        private const int MaxChangedFilesPerFolder = 2000;

        private class Session
        {
            public Guid GameId { get; set; }
            public string InstallDir { get; set; }
            public List<string> Roots { get; set; } = new List<string>();
            public Dictionary<string, long> Baseline { get; set; }
            public Task Task { get; set; }
            public DateTime StartedUtc { get; set; }
        }

        private readonly object gate = new object();
        private readonly Dictionary<Guid, Session> sessions = new Dictionary<Guid, Session>();

        /// <summary>Takes the baseline fingerprint. Returns immediately; the walk is asynchronous.</summary>
        public void Start(Guid gameId, string installDir)
        {
            var session = new Session
            {
                GameId = gameId,
                InstallDir = installDir,
                StartedUtc = DateTime.UtcNow
            };

            session.Roots.AddRange(PathTokens.LearningRoots().Where(PathTokens.DirectoryUsable));

            if (PathTokens.DirectoryUsable(installDir))
            {
                session.Roots.Add(installDir);

                var mirror = SaveScanner.VirtualStoreMirror(installDir);
                if (mirror != null)
                {
                    session.Roots.Add(mirror);
                }
            }

            lock (gate)
            {
                sessions[gameId] = session;
            }

            session.Task = Task.Factory.StartNew(() =>
            {
                try
                {
                    var map = Fingerprint(session.Roots);
                    session.Baseline = map;
                    logger.Debug("Save Vault: learning baseline has " + map.Count + " files.");
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Save Vault: learning baseline failed.");
                    session.Baseline = null;
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Compares against the baseline and returns the folders the game wrote to. Waits a
        /// bounded amount of time for the baseline walk in case a game exited immediately.
        /// </summary>
        public List<SaveTarget> Stop(Guid gameId)
        {
            Session session;
            lock (gate)
            {
                if (!sessions.TryGetValue(gameId, out session))
                {
                    return new List<SaveTarget>();
                }

                sessions.Remove(gameId);
            }

            try
            {
                if (session.Task != null && !session.Task.Wait(TimeSpan.FromSeconds(30)))
                {
                    logger.Warn("Save Vault: learning baseline did not finish in time, skipping.");
                    return new List<SaveTarget>();
                }
            }
            catch (Exception)
            {
                return new List<SaveTarget>();
            }

            if (session.Baseline == null)
            {
                return new List<SaveTarget>();
            }

            Dictionary<string, long> after;
            try
            {
                after = Fingerprint(session.Roots);
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: learning comparison failed.");
                return new List<SaveTarget>();
            }

            var changed = new List<string>();
            foreach (var pair in after)
            {
                long before;
                if (!session.Baseline.TryGetValue(pair.Key, out before) || before != pair.Value)
                {
                    changed.Add(pair.Key);
                }
            }

            if (changed.Count == 0)
            {
                return new List<SaveTarget>();
            }

            return Anchors(session, changed);
        }

        public void Clear()
        {
            lock (gate)
            {
                sessions.Clear();
            }
        }

        public bool IsWatching(Guid gameId)
        {
            lock (gate)
            {
                return sessions.ContainsKey(gameId);
            }
        }

        /// <summary>
        /// Reduces a list of changed files to a short list of folders worth backing up. A
        /// folder whose name means "saves" wins; otherwise the folder two levels below the
        /// watch root is used, which is the vendor\title layout nearly every engine follows.
        /// </summary>
        private static List<SaveTarget> Anchors(Session session, List<string> changed)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in changed)
            {
                var root = session.Roots
                    .Where(r => file.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Length)
                    .FirstOrDefault();

                if (root == null)
                {
                    continue;
                }

                var anchor = Anchor(root, file);
                if (anchor == null)
                {
                    continue;
                }

                int current;
                counts[anchor] = counts.TryGetValue(anchor, out current) ? current + 1 : 1;
            }

            var result = new List<SaveTarget>();

            foreach (var pair in counts.OrderByDescending(p => p.Value))
            {
                if (pair.Value > MaxChangedFilesPerFolder)
                {
                    // Thousands of touched files is a cache, not a save.
                    logger.Debug("Save Vault: ignoring busy folder " + pair.Key);
                    continue;
                }

                if (result.Any(t => pair.Key.StartsWith(t.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    // Already covered by a shallower anchor.
                    continue;
                }

                result.Add(new SaveTarget
                {
                    Kind = TargetKind.Folder,
                    Path = pair.Key,
                    Origin = TargetOrigin.Learned,
                    Confidence = 85,
                    Note = "observed"
                });

                if (result.Count >= 6)
                {
                    break;
                }
            }

            return result;
        }

        private static string Anchor(string root, string file)
        {
            var trimmedRoot = root.TrimEnd('\\');
            var relative = file.Substring(trimmedRoot.Length + 1);
            var parts = relative.Split('\\');
            if (parts.Length < 2)
            {
                // A file directly in a watch root; a save would not live there.
                return null;
            }

            // Prefer the deepest folder that is explicitly named like a save folder.
            for (var i = parts.Length - 2; i >= 0; i--)
            {
                if (SaveScanner.IsSaveFolderName(parts[i]))
                {
                    return trimmedRoot + "\\" + string.Join("\\", parts.Take(i + 1));
                }
            }

            var depth = Math.Min(2, parts.Length - 1);
            var segments = parts.Take(depth).ToList();

            if (segments.Any(ScanNoise.IsNoise))
            {
                return null;
            }

            return trimmedRoot + "\\" + string.Join("\\", segments);
        }

        /// <summary>
        /// Path to size-and-time map for a set of roots. Encoded as a single long so the map
        /// stays compact: sizes rarely collide with write times in practice, and a false
        /// negative here only costs one missed detection.
        /// </summary>
        private static Dictionary<string, long> Fingerprint(List<string> roots)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var budget = MaxFiles;

            foreach (var root in roots)
            {
                Walk(root, 0, map, ref budget);
                if (budget <= 0)
                {
                    logger.Warn("Save Vault: learning walk hit the file budget.");
                    break;
                }
            }

            return map;
        }

        private static void Walk(string folder, int depth, Dictionary<string, long> map, ref int budget)
        {
            if (depth > MaxDepth || budget <= 0)
            {
                return;
            }

            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    if (budget-- <= 0)
                    {
                        return;
                    }

                    try
                    {
                        var info = new FileInfo(file);
                        map[file] = info.Length ^ info.LastWriteTimeUtc.Ticks;
                    }
                    catch (Exception)
                    {
                        // Deleted between listing and reading; ignore.
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(folder);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var child in children)
            {
                if (ScanNoise.IsNoise(new DirectoryInfo(child).Name))
                {
                    continue;
                }

                Walk(child, depth + 1, map, ref budget);
                if (budget <= 0)
                {
                    return;
                }
            }
        }
    }
}
