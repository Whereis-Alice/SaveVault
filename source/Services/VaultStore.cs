using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>
    /// Owns the vault folder and its index file. Everything that touches index state goes
    /// through here under a single lock, because backups can be triggered from the game
    /// stopped event, the scheduler thread and the UI at the same time.
    ///
    /// The index lives inside the vault instead of the extension data folder on purpose: a
    /// vault that is copied to another machine or another drive stays self describing.
    /// </summary>
    public class VaultStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public const string MetaFolderName = ".savevault";

        private readonly object gate = new object();
        private readonly Func<string> rootProvider;
        private VaultIndex index;
        private string loadedRoot;

        public VaultStore(Func<string> rootProvider)
        {
            this.rootProvider = rootProvider;
        }

        public string Root
        {
            get { return rootProvider(); }
        }

        public string MetaFolder
        {
            get { return Path.Combine(Root, MetaFolderName); }
        }

        public string IndexPath
        {
            get { return Path.Combine(MetaFolder, "index.json"); }
        }

        public string LudusaviCachePath
        {
            get { return Path.Combine(MetaFolder, "ludusavi.cache.json"); }
        }

        /// <summary>Creates the vault skeleton. Returns false when the root is not writable.</summary>
        public bool EnsureRoot()
        {
            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(MetaFolder);
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: cannot create the vault root " + Root);
                return false;
            }
        }

        /// <summary>
        /// Index for the current root, reloading transparently when the user points the
        /// plugin at a different folder.
        /// </summary>
        public VaultIndex Load()
        {
            lock (gate)
            {
                var root = Root;
                if (index != null && string.Equals(loadedRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }

                index = ReadIndex();
                loadedRoot = root;
                return index;
            }
        }

        /// <summary>Drops the cached index so the next access re-reads it from disk.</summary>
        public void Invalidate()
        {
            lock (gate)
            {
                index = null;
                loadedRoot = null;
            }
        }

        private VaultIndex ReadIndex()
        {
            try
            {
                if (File.Exists(IndexPath))
                {
                    var loaded = Serialization.FromJson<VaultIndex>(File.ReadAllText(IndexPath));
                    if (loaded != null)
                    {
                        if (loaded.Games == null)
                        {
                            loaded.Games = new List<GameSaveProfile>();
                        }

                        foreach (var game in loaded.Games)
                        {
                            if (game.Targets == null)
                            {
                                game.Targets = new List<SaveTarget>();
                            }

                            if (game.Snapshots == null)
                            {
                                game.Snapshots = new List<SnapshotRecord>();
                            }
                        }

                        return loaded;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: the vault index could not be read, starting a new one.");
                TryBackupBrokenIndex();
            }

            return new VaultIndex();
        }

        /// <summary>
        /// Keeps a corrupt index around instead of overwriting it silently. A broken index
        /// only costs a rescan, but the snapshot notes and pins in it are not recoverable.
        /// </summary>
        private void TryBackupBrokenIndex()
        {
            try
            {
                if (File.Exists(IndexPath))
                {
                    var target = IndexPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(IndexPath, target, true);
                }
            }
            catch (Exception)
            {
                // Nothing useful to do if even the copy fails.
            }
        }

        /// <summary>Writes the index through a temporary file so a crash cannot truncate it.</summary>
        public void Save()
        {
            lock (gate)
            {
                if (index == null)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(MetaFolder);
                    var temp = IndexPath + ".tmp";
                    File.WriteAllText(temp, Serialization.ToJson(index, true), Encoding.UTF8);

                    if (File.Exists(IndexPath))
                    {
                        File.Delete(IndexPath);
                    }

                    File.Move(temp, IndexPath);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Save Vault: failed to write the vault index.");
                }
            }
        }

        /// <summary>Existing profile for a game, or null.</summary>
        public GameSaveProfile Find(Guid gameId)
        {
            lock (gate)
            {
                return Load().Games.FirstOrDefault(g => g.GameId == gameId);
            }
        }

        /// <summary>
        /// Profile for a game, created on first use. The vault folder name is assigned once
        /// and then kept, so renaming a game in Playnite does not orphan its snapshots.
        /// </summary>
        public GameSaveProfile GetOrCreate(Game game)
        {
            if (game == null)
            {
                return null;
            }

            lock (gate)
            {
                var loaded = Load();
                var profile = loaded.Games.FirstOrDefault(g => g.GameId == game.Id);
                if (profile == null)
                {
                    profile = new GameSaveProfile
                    {
                        GameId = game.Id,
                        Name = game.Name,
                        Folder = PathTokens.SanitizeFolderName(game.Name, game.Id)
                    };

                    loaded.Games.Add(profile);
                }
                else
                {
                    profile.Name = game.Name;
                    if (string.IsNullOrEmpty(profile.Folder))
                    {
                        profile.Folder = PathTokens.SanitizeFolderName(game.Name, game.Id);
                    }
                }

                return profile;
            }
        }

        public IEnumerable<GameSaveProfile> Profiles()
        {
            lock (gate)
            {
                return Load().Games.ToList();
            }
        }

        public string ProfileFolder(GameSaveProfile profile)
        {
            return Path.Combine(Root, profile.Folder);
        }

        public string SnapshotPath(GameSaveProfile profile, SnapshotRecord snapshot)
        {
            return Path.Combine(ProfileFolder(profile), snapshot.Id + ".zip");
        }

        /// <summary>Sum of the recorded snapshot sizes across the whole vault.</summary>
        public long TotalBytes()
        {
            lock (gate)
            {
                return Load().Games.Sum(g => g.Snapshots.Sum(s => s.Bytes));
            }
        }

        /// <summary>
        /// Removes index entries whose archive is gone and adopts archives that exist on disk
        /// but are missing from the index. Runs cheaply on start up so a vault edited by hand
        /// or restored from a file backup does not report phantom snapshots.
        /// </summary>
        public int Reconcile()
        {
            lock (gate)
            {
                var changes = 0;
                var loaded = Load();

                foreach (var profile in loaded.Games)
                {
                    var folder = ProfileFolder(profile);
                    var missing = profile.Snapshots.Where(s => !File.Exists(SnapshotPath(profile, s))).ToList();
                    foreach (var gone in missing)
                    {
                        profile.Snapshots.Remove(gone);
                        changes++;
                    }

                    if (!Directory.Exists(folder))
                    {
                        continue;
                    }

                    foreach (var file in SafeFiles(folder, "*.zip"))
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        if (profile.Snapshots.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var info = new FileInfo(file);
                        profile.Snapshots.Add(new SnapshotRecord
                        {
                            Id = id,
                            CreatedUtc = info.LastWriteTimeUtc,
                            Trigger = SnapshotTrigger.Manual,
                            Bytes = info.Length,
                            Note = "adopted"
                        });

                        changes++;
                    }

                    profile.Snapshots.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
                }

                if (changes > 0)
                {
                    Save();
                }

                return changes;
            }
        }

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try
            {
                return Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                return new string[0];
            }
        }
    }
}
