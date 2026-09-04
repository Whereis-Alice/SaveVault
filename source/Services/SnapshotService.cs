using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>Description of one archived target, written into the snapshot itself.</summary>
    public class SnapshotSource
    {
        [SerializationPropertyName("index")]
        public int Index { get; set; }

        [SerializationPropertyName("kind")]
        public TargetKind Kind { get; set; }

        [SerializationPropertyName("path")]
        public string Path { get; set; }

        [SerializationPropertyName("filter")]
        public string Filter { get; set; }

        [SerializationPropertyName("origin")]
        public TargetOrigin Origin { get; set; }
    }

    /// <summary>
    /// The snapshot.json stored inside every archive. A snapshot must be restorable with
    /// nothing but the zip file, otherwise a lost index would turn the whole vault into a
    /// pile of anonymous archives.
    /// </summary>
    public class SnapshotManifest
    {
        public const int CurrentSchema = 1;

        [SerializationPropertyName("schema")]
        public int Schema { get; set; } = CurrentSchema;

        [SerializationPropertyName("plugin")]
        public string Plugin { get; set; }

        [SerializationPropertyName("gameId")]
        public Guid GameId { get; set; }

        [SerializationPropertyName("gameName")]
        public string GameName { get; set; }

        [SerializationPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        [SerializationPropertyName("trigger")]
        public SnapshotTrigger Trigger { get; set; }

        [SerializationPropertyName("hash")]
        public string Hash { get; set; }

        [SerializationPropertyName("sources")]
        public List<SnapshotSource> Sources { get; set; } = new List<SnapshotSource>();
    }

    /// <summary>Outcome of a restore, reported to the user.</summary>
    public class RestoreResult
    {
        public int Files { get; set; }
        public int RegistryKeys { get; set; }
        public long Bytes { get; set; }
        public string Error { get; set; }
        public string UndoSnapshotId { get; set; }

        public bool Failed
        {
            get { return !string.IsNullOrEmpty(Error); }
        }
    }

    /// <summary>
    /// Creates, restores and prunes snapshots. Archives are plain zip files with a
    /// predictable layout:
    ///
    ///   snapshot.json          self describing metadata, enough to restore standalone
    ///   targets/0_save/...     one folder per file target, original tree preserved
    ///   registry/0.reg         one exported key per registry target
    ///
    /// A content hash over the source file list lets an unchanged game be skipped, which is
    /// what makes an every-session trigger practical inside a 2 GB budget.
    /// </summary>
    public class SnapshotService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly VaultStore store;
        private readonly SaveVaultSettings settings;

        public SnapshotService(VaultStore store, SaveVaultSettings settings)
        {
            this.store = store;
            this.settings = settings;
        }

        private string TempFolder
        {
            get { return Path.Combine(store.MetaFolder, "temp"); }
        }

        // ------------------------------------------------------------------------- backup

        /// <summary>
        /// Archives the enabled targets of a profile. Nothing is written when the content
        /// hash matches the previous snapshot, unless the caller forces it.
        /// </summary>
        public BackupResult Backup(Game game, GameSaveProfile profile, SnapshotTrigger trigger, bool force)
        {
            var result = new BackupResult { GameName = profile.Name };

            var targets = profile.Targets
                .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Path))
                .ToList();

            if (targets.Count == 0)
            {
                result.NoTargets = true;
                return result;
            }

            if (!store.EnsureRoot())
            {
                result.Error = Localization.Get("LOCSaveVaultErrorRoot", "The vault folder is not writable.");
                return result;
            }

            var work = Path.Combine(TempFolder, Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                Directory.CreateDirectory(work);

                var plan = BuildPlan(targets, work);
                if (plan.Count == 0)
                {
                    result.NoTargets = true;
                    return result;
                }

                foreach (var item in plan)
                {
                    result.SourceFiles += item.Files.Count;
                    result.SourceBytes += item.Files.Sum(f => f.Length);
                }

                // Measured before anything is written: a folder that only looks like a save
                // location can be enormous, and the honest answer is to refuse it rather than
                // spend the whole vault budget on one game. The caps are settings, so a game with
                // genuinely huge saves is one number away from being backed up.
                var byteCap = (long)Math.Max(0, settings.MaxSnapshotMegabytes) * 1024L * 1024L;
                var fileCap = Math.Max(0, settings.MaxSnapshotFiles);

                if ((byteCap > 0 && result.SourceBytes > byteCap) ||
                    (fileCap > 0 && result.SourceFiles > fileCap))
                {
                    result.TooLarge = true;
                    logger.Warn("Save Vault: skipped " + profile.Name + ", sources are " + result.SourceFiles +
                                " files / " + result.SourceBytes + " bytes, over the per snapshot budget.");
                    return result;
                }

                var hash = ComputeHash(plan);
                if (!force && string.Equals(hash, profile.LastHash, StringComparison.OrdinalIgnoreCase) &&
                    profile.Snapshots.Count > 0)
                {
                    result.Unchanged = true;
                    return result;
                }

                var id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + trigger;
                var folder = store.ProfileFolder(profile);
                Directory.CreateDirectory(folder);

                var archive = Path.Combine(folder, id + ".zip");
                if (File.Exists(archive))
                {
                    id = id + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                    archive = Path.Combine(folder, id + ".zip");
                }

                var manifest = new SnapshotManifest
                {
                    Plugin = "SaveVault",
                    GameId = profile.GameId,
                    GameName = profile.Name,
                    CreatedUtc = DateTime.UtcNow,
                    Trigger = trigger,
                    Hash = hash
                };

                var files = 0;
                var bytes = 0L;

                using (var stream = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
                {
                    var level = settings.CompressSnapshots ? CompressionLevel.Optimal : CompressionLevel.NoCompression;

                    foreach (var item in plan)
                    {
                        manifest.Sources.Add(new SnapshotSource
                        {
                            Index = item.Index,
                            Kind = item.Target.Kind,
                            Path = item.Target.Path,
                            Filter = item.Target.Filter,
                            Origin = item.Target.Origin
                        });

                        if (item.Target.IsRegistry)
                        {
                            var entryName = "registry/" + item.Index + ".reg";
                            zip.CreateEntryFromFile(item.RegistryFile, entryName, level);
                            files++;
                            bytes += new FileInfo(item.RegistryFile).Length;
                            continue;
                        }

                        var prefix = "targets/" + item.Index + "_" + SafeLeaf(item.Target.Path) + "/";
                        foreach (var file in item.Files)
                        {
                            var relative = FileWalker.RelativePath(item.Target.Path, file.FullName);
                            var entryName = prefix + relative.Replace('\\', '/');

                            try
                            {
                                zip.CreateEntryFromFile(file.FullName, entryName, level);
                                files++;
                                bytes += file.Length;
                            }
                            catch (Exception e)
                            {
                                logger.Warn(e, "Save Vault: skipped a locked file " + file.FullName);
                            }
                        }
                    }

                    var meta = zip.CreateEntry("snapshot.json", level);
                    using (var writer = new StreamWriter(meta.Open(), new UTF8Encoding(false)))
                    {
                        writer.Write(Serialization.ToJson(manifest, true));
                    }
                }

                var archiveInfo = new FileInfo(archive);

                var record = new SnapshotRecord
                {
                    Id = id,
                    CreatedUtc = manifest.CreatedUtc,
                    Trigger = trigger,
                    Bytes = archiveInfo.Length,
                    Files = files,
                    Hash = hash,
                    Sources = plan.Select(p => p.Target.Clone()).ToList()
                };

                profile.Snapshots.Insert(0, record);
                profile.LastBackupUtc = record.CreatedUtc;
                profile.LastHash = hash;

                result.Created = true;
                result.Snapshot = record;

                Prune(profile);
                store.Save();

                logger.Info("Save Vault: snapshot " + id + " for " + profile.Name + ", " + files + " files, " +
                            archiveInfo.Length + " bytes (sources " + bytes + ").");
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: backup failed for " + profile.Name);
                result.Error = e.Message;
            }
            finally
            {
                SafeDeleteFolder(work);
            }

            return result;
        }

        private class PlanItem
        {
            public int Index { get; set; }
            public SaveTarget Target { get; set; }
            public List<FileInfo> Files { get; set; } = new List<FileInfo>();
            public string RegistryFile { get; set; }
        }

        /// <summary>
        /// Resolves what would go into a snapshot. Registry keys are exported first, into the
        /// vault's own temp folder rather than the system temp directory, so that a small
        /// system drive is never touched by a backup.
        /// </summary>
        private List<PlanItem> BuildPlan(List<SaveTarget> targets, string work)
        {
            var plan = new List<PlanItem>();
            var index = 0;

            foreach (var target in targets)
            {
                if (target.IsRegistry)
                {
                    if (!settings.IncludeRegistry)
                    {
                        continue;
                    }

                    var file = Path.Combine(work, index + ".reg");
                    if (!RegistryBridge.Export(target.Path, file))
                    {
                        continue;
                    }

                    plan.Add(new PlanItem { Index = index, Target = target, RegistryFile = file });
                    index++;
                    continue;
                }

                if (!PathTokens.DirectoryUsable(target.Path))
                {
                    continue;
                }

                var files = FileWalker.Enumerate(target.Path, target.Filter).ToList();
                if (files.Count == 0)
                {
                    continue;
                }

                plan.Add(new PlanItem { Index = index, Target = target, Files = files });
                index++;
            }

            return plan;
        }

        /// <summary>
        /// Fingerprint of the sources: relative path, size and write time of every file, plus
        /// the exported bytes of every registry key. Content is not read, so the check stays
        /// fast on large folders while still catching any real save.
        /// </summary>
        private static string ComputeHash(List<PlanItem> plan)
        {
            var lines = new List<string>();

            foreach (var item in plan)
            {
                if (item.Target.IsRegistry)
                {
                    try
                    {
                        var text = File.ReadAllText(item.RegistryFile);
                        lines.Add("reg|" + item.Target.Path.ToLowerInvariant() + "|" + Sha1(text));
                    }
                    catch (Exception)
                    {
                        lines.Add("reg|" + item.Target.Path.ToLowerInvariant() + "|error");
                    }

                    continue;
                }

                foreach (var file in item.Files)
                {
                    var relative = FileWalker.RelativePath(item.Target.Path, file.FullName);
                    lines.Add(item.Index + "|" + relative.ToLowerInvariant() + "|" + file.Length + "|" +
                              file.LastWriteTimeUtc.Ticks);
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return Sha1(string.Join("\n", lines));
        }

        private static string Sha1(string text)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static string SafeLeaf(string path)
        {
            var leaf = path.TrimEnd('\\', '/');
            var index = leaf.LastIndexOfAny(new[] { '\\', '/' });
            if (index >= 0)
            {
                leaf = leaf.Substring(index + 1);
            }

            var builder = new StringBuilder();
            foreach (var c in leaf)
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            var text = builder.ToString().Trim('_');
            if (text.Length > 24)
            {
                text = text.Substring(0, 24);
            }

            return text.Length == 0 ? "target" : text;
        }

        // ------------------------------------------------------------------------ restore

        /// <summary>
        /// Writes a snapshot back over the live save locations. A safety snapshot is taken
        /// first, and registry keys are exported to an undo folder before they are imported,
        /// so a restore is never a one way door.
        /// </summary>
        public RestoreResult Restore(Game game, GameSaveProfile profile, SnapshotRecord snapshot)
        {
            var result = new RestoreResult();
            var archive = store.SnapshotPath(profile, snapshot);

            if (!File.Exists(archive))
            {
                result.Error = Localization.Get("LOCSaveVaultErrorArchiveMissing", "The snapshot file is missing.");
                return result;
            }

            try
            {
                var undo = Backup(game, profile, SnapshotTrigger.BeforeRestore, true);
                if (undo.Created && undo.Snapshot != null)
                {
                    result.UndoSnapshotId = undo.Snapshot.Id;
                }

                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Read))
                {
                    var manifest = ReadManifest(zip) ?? FallbackManifest(profile, snapshot);
                    var map = manifest.Sources.ToDictionary(s => s.Index, s => s);

                    var undoFolder = Path.Combine(store.MetaFolder, "registry-undo",
                        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName == "snapshot.json" || entry.Length == 0 && entry.Name.Length == 0)
                        {
                            continue;
                        }

                        if (entry.FullName.StartsWith("registry/", StringComparison.OrdinalIgnoreCase))
                        {
                            if (RestoreRegistry(zip, entry, map, undoFolder))
                            {
                                result.RegistryKeys++;
                            }

                            continue;
                        }

                        if (!entry.FullName.StartsWith("targets/", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var relative = entry.FullName.Substring("targets/".Length);
                        var slash = relative.IndexOf('/');
                        if (slash <= 0)
                        {
                            continue;
                        }

                        var folderName = relative.Substring(0, slash);
                        var tail = relative.Substring(slash + 1).Replace('/', '\\');
                        if (tail.Length == 0)
                        {
                            continue;
                        }

                        var index = ParseIndex(folderName);
                        SnapshotSource source;
                        if (index < 0 || !map.TryGetValue(index, out source) || string.IsNullOrEmpty(source.Path))
                        {
                            continue;
                        }

                        var destination = Path.Combine(source.Path, tail);
                        var parent = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        entry.ExtractToFile(destination, true);
                        result.Files++;
                        result.Bytes += entry.Length;
                    }
                }

                logger.Info("Save Vault: restored " + snapshot.Id + " for " + profile.Name + ", " + result.Files +
                            " files, " + result.RegistryKeys + " registry keys.");
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: restore failed for " + profile.Name);
                result.Error = e.Message;
            }

            return result;
        }

        private bool RestoreRegistry(ZipArchive zip, ZipArchiveEntry entry, Dictionary<int, SnapshotSource> map, string undoFolder)
        {
            var index = ParseIndex(Path.GetFileNameWithoutExtension(entry.Name));
            SnapshotSource source;
            if (index < 0 || !map.TryGetValue(index, out source) || string.IsNullOrEmpty(source.Path))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(undoFolder);

                // Always keep an escape hatch, even when registry backups are switched off.
                RegistryBridge.Export(source.Path, Path.Combine(undoFolder, index + ".reg"));

                var temp = Path.Combine(undoFolder, "apply-" + index + ".reg");
                entry.ExtractToFile(temp, true);
                var ok = RegistryBridge.Import(temp);
                File.Delete(temp);
                return ok;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: registry restore failed for " + source.Path);
                return false;
            }
        }

        private static int ParseIndex(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return -1;
            }

            var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            int value;
            return int.TryParse(digits, out value) ? value : -1;
        }

        /// <summary>Reads snapshot.json out of an archive, or null when it is not there.</summary>
        public static SnapshotManifest ReadManifest(ZipArchive zip)
        {
            var entry = zip.GetEntry("snapshot.json");
            if (entry == null)
            {
                return null;
            }

            try
            {
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                {
                    return Serialization.FromJson<SnapshotManifest>(reader.ReadToEnd());
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: unreadable snapshot metadata.");
                return null;
            }
        }

        /// <summary>Rebuilds metadata from the index for archives written before this schema.</summary>
        private static SnapshotManifest FallbackManifest(GameSaveProfile profile, SnapshotRecord snapshot)
        {
            var manifest = new SnapshotManifest
            {
                GameId = profile.GameId,
                GameName = profile.Name,
                CreatedUtc = snapshot.CreatedUtc,
                Trigger = snapshot.Trigger,
                Hash = snapshot.Hash
            };

            for (var i = 0; i < snapshot.Sources.Count; i++)
            {
                manifest.Sources.Add(new SnapshotSource
                {
                    Index = i,
                    Kind = snapshot.Sources[i].Kind,
                    Path = snapshot.Sources[i].Path,
                    Filter = snapshot.Sources[i].Filter,
                    Origin = snapshot.Sources[i].Origin
                });
            }

            return manifest;
        }

        // -------------------------------------------------------------------- retention

        /// <summary>
        /// Retention for one game: pinned snapshots and the newest one are untouchable, then
        /// one snapshot per recent day and per recent week is preferred, and the remainder is
        /// dropped oldest first until the per game count fits.
        /// </summary>
        public void Prune(GameSaveProfile profile)
        {
            var snapshots = profile.Snapshots.OrderByDescending(s => s.CreatedUtc).ToList();
            if (snapshots.Count == 0)
            {
                return;
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { snapshots[0].Id };

            foreach (var pinned in snapshots.Where(s => s.Pinned))
            {
                keep.Add(pinned.Id);
            }

            var now = DateTime.UtcNow;
            var days = new HashSet<string>();
            var weeks = new HashSet<string>();

            foreach (var snapshot in snapshots)
            {
                var age = now - snapshot.CreatedUtc;

                if (age.TotalDays <= Math.Max(0, settings.KeepDaily))
                {
                    var day = snapshot.CreatedUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    if (days.Add(day))
                    {
                        keep.Add(snapshot.Id);
                    }
                }

                if (age.TotalDays <= Math.Max(0, settings.KeepWeekly) * 7)
                {
                    var week = snapshot.CreatedUtc.Year + "-" +
                               (snapshot.CreatedUtc.DayOfYear / 7).ToString(CultureInfo.InvariantCulture);
                    if (weeks.Add(week))
                    {
                        keep.Add(snapshot.Id);
                    }
                }
            }

            var max = Math.Max(1, settings.MaxSnapshotsPerGame);

            // The count cap wins over the daily and weekly preferences, oldest first.
            if (keep.Count > max)
            {
                foreach (var snapshot in snapshots.OrderBy(s => s.CreatedUtc))
                {
                    if (keep.Count <= max)
                    {
                        break;
                    }

                    if (snapshot.Pinned || snapshot.Id == snapshots[0].Id)
                    {
                        continue;
                    }

                    keep.Remove(snapshot.Id);
                }
            }

            foreach (var snapshot in snapshots)
            {
                if (!keep.Contains(snapshot.Id))
                {
                    Delete(profile, snapshot);
                }
            }
        }

        /// <summary>
        /// Vault wide budget. Oldest first across all games, never touching a pinned snapshot
        /// or the only snapshot a game has, so filling the quota cannot leave a game unprotected.
        /// </summary>
        public long EnforceQuota()
        {
            var cap = (long)Math.Max(64, settings.MaxTotalMegabytes) * 1024L * 1024L;
            var freed = 0L;
            var guard = 0;

            while (store.TotalBytes() > cap && guard++ < 5000)
            {
                var candidates = new List<Tuple<GameSaveProfile, SnapshotRecord>>();

                foreach (var profile in store.Profiles())
                {
                    var ordered = profile.Snapshots.OrderByDescending(s => s.CreatedUtc).ToList();
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        if (!ordered[i].Pinned)
                        {
                            candidates.Add(Tuple.Create(profile, ordered[i]));
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    break;
                }

                var oldest = candidates.OrderBy(c => c.Item2.CreatedUtc).First();
                freed += oldest.Item2.Bytes;
                Delete(oldest.Item1, oldest.Item2);
            }

            if (freed > 0)
            {
                store.Save();
                logger.Info("Save Vault: quota pruning freed " + freed + " bytes.");
            }

            return freed;
        }

        /// <summary>
        /// How far the vault is over its budget, in bytes, or 0 when it fits.
        ///
        /// Worth reporting because the quota cannot always be met: the pruner refuses to delete
        /// the only snapshot a game has, so a library of many games each holding one large
        /// snapshot legitimately stays over the limit. Saying so beats pretending.
        /// </summary>
        public long Overflow()
        {
            var cap = (long)Math.Max(64, settings.MaxTotalMegabytes) * 1024L * 1024L;
            return Math.Max(0, store.TotalBytes() - cap);
        }

        /// <summary>The games taking the most room, largest first, for a hint the user can act on.</summary>
        public List<KeyValuePair<string, long>> Largest(int count)
        {
            return store.Profiles()
                .Select(p => new KeyValuePair<string, long>(p.Name, p.Snapshots.Sum(s => s.Bytes)))
                .Where(p => p.Value > 0)
                .OrderByDescending(p => p.Value)
                .Take(Math.Max(1, count))
                .ToList();
        }

        /// <summary>Removes a snapshot from disk and from the index.</summary>
        public bool Delete(GameSaveProfile profile, SnapshotRecord snapshot)
        {
            try
            {
                var path = store.SnapshotPath(profile, snapshot);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                profile.Snapshots.RemoveAll(s => string.Equals(s.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
                return true;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: could not delete snapshot " + snapshot.Id);
                return false;
            }
        }

        private static void SafeDeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception)
            {
                // A leftover temp folder is harmless; it is reused and cleaned next time.
            }
        }
    }
}
