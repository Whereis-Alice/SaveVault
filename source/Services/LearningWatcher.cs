using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>
    /// Learns save locations by watching what a game actually writes.
    ///
    /// A fingerprint of the user folders is taken when the game starts and compared when it
    /// stops. This is the only layer that works for titles no database has ever heard of,
    /// which in a Japanese visual novel library is most of them.
    ///
    /// What changed after 1.0.0: a changed folder is no longer a save location by itself.
    /// Every other program on the machine writes to AppData while a game is open, so the diff
    /// also contains the messenger, the driver panel and a couple of Electron caches. Each
    /// candidate now has to pass <see cref="LearnedGuard" />, which asks it to prove it belongs
    /// to the game that was running and to stay within the candidate size caps.
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
        private const int MaxLearnedPerSession = 6;
        private const int MaxSamplesPerFolder = 512;

        private class Session
        {
            public Guid GameId { get; set; }
            public string Name { get; set; }
            public LearnContext Context { get; set; }
            public List<string> Roots { get; set; } = new List<string>();
            public List<string> Blocked { get; set; } = new List<string>();
            public Dictionary<string, long> Baseline { get; set; }
            public Task Task { get; set; }
            public DateTime StartedUtc { get; set; }
        }

        /// <summary>Changed files below one candidate folder, sampled rather than kept whole.</summary>
        private class Bucket
        {
            public int Count { get; set; }
            public List<string> Samples { get; } = new List<string>();

            public void Add(string file)
            {
                Count++;
                if (Samples.Count < MaxSamplesPerFolder)
                {
                    Samples.Add(file);
                }
            }
        }

        private readonly SaveVaultSettings settings;
        private readonly object gate = new object();
        private readonly Dictionary<Guid, Session> sessions = new Dictionary<Guid, Session>();

        public LearningWatcher(SaveVaultSettings settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// Takes the baseline fingerprint. Returns immediately; the walk is asynchronous.
        /// The game is needed in full, not just its id: the title is what a candidate folder
        /// is later held against.
        /// </summary>
        public void Start(Game game)
        {
            if (game == null)
            {
                return;
            }

            var installDir = SaveScanner.SafeInstallDir(game);
            var session = new Session
            {
                GameId = game.Id,
                Name = game.Name,
                Context = LearnContext.For(game, settings),
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

            // Two folders are guaranteed to change during every session and are never a save:
            // the vault itself and Playnite's configuration. Skipping them keeps the walk
            // cheaper as well.
            session.Blocked.Add(session.Context.VaultRoot);
            session.Blocked.Add(LearnedGuard.PlayniteConfigFolder());
            session.Blocked = session.Blocked
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b.TrimEnd('\\'))
                .ToList();

            lock (gate)
            {
                sessions[game.Id] = session;
            }

            session.Task = Task.Factory.StartNew(() =>
            {
                try
                {
                    var map = Fingerprint(session);
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
                after = Fingerprint(session);
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
        /// Whatever comes out of that still has to convince <see cref="LearnedGuard" />.
        /// </summary>
        private static List<SaveTarget> Anchors(Session session, List<string> changed)
        {
            var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);

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

                Bucket bucket;
                if (!buckets.TryGetValue(anchor, out bucket))
                {
                    bucket = new Bucket();
                    buckets[anchor] = bucket;
                }

                bucket.Add(file);
            }

            var result = new List<SaveTarget>();
            var rejected = 0;

            foreach (var pair in buckets.OrderByDescending(p => p.Value.Count))
            {
                if (pair.Value.Count > MaxChangedFilesPerFolder)
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

                string reason;
                if (!LearnedGuard.PlausibleObserved(pair.Key, session.Context, pair.Value.Samples, out reason))
                {
                    rejected++;
                    logger.Debug("Save Vault: not learning " + pair.Key + " for " + session.Name + " (" + reason + ").");
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

                if (result.Count >= MaxLearnedPerSession)
                {
                    break;
                }
            }

            if (rejected > 0)
            {
                logger.Info("Save Vault: " + rejected + " observed folder(s) did not belong to " + session.Name + ".");
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
        private static Dictionary<string, long> Fingerprint(Session session)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var budget = MaxFiles;

            foreach (var root in session.Roots)
            {
                Walk(root, 0, map, ref budget, session.Blocked);
                if (budget <= 0)
                {
                    logger.Warn("Save Vault: learning walk hit the file budget.");
                    break;
                }
            }

            return map;
        }

        private static void Walk(string folder, int depth, Dictionary<string, long> map, ref int budget, List<string> blocked)
        {
            if (depth > MaxDepth || budget <= 0)
            {
                return;
            }

            if (blocked.Any(b => string.Equals(folder.TrimEnd('\\'), b, StringComparison.OrdinalIgnoreCase)))
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

                Walk(child, depth + 1, map, ref budget, blocked);
                if (budget <= 0)
                {
                    return;
                }
            }
        }
    }
}
