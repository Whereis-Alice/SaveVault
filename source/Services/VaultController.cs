using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using SaveVault.Models;

namespace SaveVault.Services
{
    /// <summary>
    /// Single entry point for every vault operation, shared by the game menu, the main menu,
    /// the details panel and the manager window.
    ///
    /// Keeping detection, backup, restore and messaging in one place is what lets the same
    /// action behave identically no matter where it was triggered, and it is the only way to
    /// guarantee that a background trigger and a click cannot run the same backup twice.
    /// </summary>
    public class VaultController
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly IPlayniteAPI api;
        private readonly SaveVaultSettings settings;
        private readonly VaultStore store;
        private readonly SnapshotService snapshots;
        private readonly LudusaviManifest manifest = new LudusaviManifest();

        private readonly object manifestGate = new object();
        private readonly object workGate = new object();
        private bool manifestReady;

        public VaultController(IPlayniteAPI api, SaveVaultSettings settings, VaultStore store)
        {
            this.api = api;
            this.settings = settings;
            this.store = store;
            snapshots = new SnapshotService(store, settings);
        }

        /// <summary>Raised after anything changed that the UI shows.</summary>
        public event EventHandler Changed;

        public SaveVaultSettings Settings
        {
            get { return settings; }
        }

        public VaultStore Store
        {
            get { return store; }
        }

        public SnapshotService Snapshots
        {
            get { return snapshots; }
        }

        public LudusaviManifest Manifest
        {
            get { return manifest; }
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>Loads the Ludusavi manifest on first use, at most once per session.</summary>
        public void EnsureManifest()
        {
            if (!settings.UseLudusaviManifest)
            {
                return;
            }

            lock (manifestGate)
            {
                if (manifestReady)
                {
                    return;
                }

                manifestReady = true;
                store.EnsureRoot();
                manifest.Load(settings.LudusaviManifestPath, store.LudusaviCachePath);
            }
        }

        /// <summary>Forces the next detection to re-read the manifest, after a settings change.</summary>
        public void ResetManifest()
        {
            lock (manifestGate)
            {
                manifestReady = false;
            }
        }

        // -------------------------------------------------------------------- detection

        /// <summary>
        /// Runs detection for one game and merges the result into its profile. Returns the
        /// number of newly discovered targets.
        /// </summary>
        public int Detect(Game game, bool save = true)
        {
            if (game == null)
            {
                return 0;
            }

            EnsureManifest();

            var profile = store.GetOrCreate(game);
            var scanner = new SaveScanner(settings, manifest);
            var found = scanner.Scan(game);
            var added = SaveScanner.Merge(profile, found);
            profile.ScannedUtc = DateTime.UtcNow;

            if (save)
            {
                store.Save();
                RaiseChanged();
            }

            logger.Info("Save Vault: detection for " + game.Name + " found " + found.Count + " targets, " + added + " new.");
            return added;
        }

        /// <summary>Detection with a progress dialog and a summary message.</summary>
        public void DetectInteractive(Game game)
        {
            var added = 0;
            RunWithProgress(Localization.Get("LOCSaveVaultProgressDetect", "Looking for save locations") + " - " + game.Name,
                () => { added = Detect(game); });

            var profile = store.Find(game.Id);
            var total = profile == null ? 0 : profile.Targets.Count;

            Info(Localization.Get("LOCSaveVaultDetectDone", "Detection finished.") + "\n" +
                 Localization.Get("LOCSaveVaultDetectTotal", "Known locations:") + " " + total + "  (+" + added + ")");
        }

        /// <summary>Detection across the whole library, used from the main menu.</summary>
        public void DetectAllInteractive()
        {
            var games = api.Database.Games.Where(g => !g.Hidden).ToList();
            var withTargets = 0;
            var added = 0;

            RunWithProgress(Localization.Get("LOCSaveVaultProgressDetectAll", "Looking for save locations in the whole library"),
                progress =>
                {
                    if (progress != null)
                    {
                        progress.ProgressMaxValue = games.Count;
                    }

                    foreach (var game in games)
                    {
                        if (progress != null)
                        {
                            if (progress.CancelToken.IsCancellationRequested)
                            {
                                break;
                            }

                            progress.Text = game.Name;
                            progress.CurrentProgressValue++;
                        }

                        added += Detect(game, false);

                        var profile = store.Find(game.Id);
                        if (profile != null && profile.HasTargets)
                        {
                            withTargets++;
                        }
                    }
                });

            store.Save();
            RaiseChanged();

            Info(Localization.Get("LOCSaveVaultDetectAllDone", "Library scan finished.") + "\n" +
                 Localization.Get("LOCSaveVaultDetectAllGames", "Games with a known save location:") + " " + withTargets + "/" + games.Count + "  (+" + added + ")");
        }

        // ----------------------------------------------------------------------- backup

        /// <summary>
        /// Backs a game up, detecting first when nothing is known yet. Serialised against
        /// every other vault operation so overlapping triggers cannot corrupt an archive.
        /// </summary>
        public BackupResult Backup(Game game, SnapshotTrigger trigger, bool force)
        {
            if (game == null)
            {
                return new BackupResult { Skipped = true };
            }

            lock (workGate)
            {
                var profile = store.GetOrCreate(game);

                if (profile.Excluded && trigger != SnapshotTrigger.Manual && trigger != SnapshotTrigger.BeforeRestore)
                {
                    return new BackupResult { Skipped = true, GameName = profile.Name };
                }

                if (!profile.HasTargets && settings.AutoScanNewGames)
                {
                    Detect(game, false);
                }

                var result = snapshots.Backup(game, profile, trigger, force);
                if (result.Created)
                {
                    snapshots.EnforceQuota();
                }

                store.Save();
                RaiseChanged();
                return result;
            }
        }

        /// <summary>Backup triggered by a click: progress dialog, then a summary.</summary>
        public void BackupInteractive(Game game, bool force = true)
        {
            BackupResult result = null;
            RunWithProgress(Localization.Get("LOCSaveVaultProgressBackup", "Backing up") + " - " + game.Name,
                () => { result = Backup(game, SnapshotTrigger.Manual, force); });

            if (result == null)
            {
                return;
            }

            if (result.Failed)
            {
                Error(Localization.Get("LOCSaveVaultBackupFailed", "Backup failed.") + "\n" + result.Error);
                return;
            }

            if (result.NoTargets)
            {
                Info(Localization.Get("LOCSaveVaultNoTargets",
                    "No save location is known for this game yet. Run detection, play it once with learning enabled, or add a path by hand."));
                return;
            }

            if (result.TooLarge)
            {
                Info(Localization.Fill("LOCSaveVaultTooLarge",
                        "The save location of this game holds {0}, more than the per snapshot limit of {1}. Nothing was written.",
                        FormatSize(result.SourceBytes) + " / " + result.SourceFiles + " " +
                            Localization.Get("LOCSaveVaultFilesWord", "files"),
                        FormatSize((long)settings.MaxSnapshotMegabytes * 1024L * 1024L)) + "\n\n" +
                     Localization.Get("LOCSaveVaultTooLargeHint",
                        "Remove the location that is too big in the manager, narrow it with a file filter, or raise the limit in the settings."));
                return;
            }

            if (result.Unchanged)
            {
                Info(Localization.Get("LOCSaveVaultUnchanged", "Saves are unchanged since the last snapshot."));
                return;
            }

            Info(Localization.Get("LOCSaveVaultBackupDone", "Snapshot created.") + "\n" +
                 result.Snapshot.Id + "  ·  " + result.Snapshot.Files + " " +
                 Localization.Get("LOCSaveVaultFilesWord", "files") + "  ·  " + FormatSize(result.Snapshot.Bytes));
        }

        /// <summary>Whole library backup from the main menu.</summary>
        public void BackupAllInteractive()
        {
            var games = api.Database.Games.Where(g => !g.Hidden).ToList();
            var created = 0;
            var unchanged = 0;
            var empty = 0;
            var failed = 0;
            var oversized = 0;

            RunWithProgress(Localization.Get("LOCSaveVaultProgressBackupAll", "Backing up the whole library"),
                progress =>
                {
                    if (progress != null)
                    {
                        progress.ProgressMaxValue = games.Count;
                    }

                    foreach (var game in games)
                    {
                        if (progress != null)
                        {
                            if (progress.CancelToken.IsCancellationRequested)
                            {
                                break;
                            }

                            progress.Text = game.Name;
                            progress.CurrentProgressValue++;
                        }

                        var result = Backup(game, SnapshotTrigger.Scheduled, false);
                        if (result.Created)
                        {
                            created++;
                        }
                        else if (result.Unchanged)
                        {
                            unchanged++;
                        }
                        else if (result.NoTargets)
                        {
                            empty++;
                        }
                        else if (result.Failed)
                        {
                            failed++;
                        }
                        else if (result.TooLarge)
                        {
                            oversized++;
                        }
                    }
                });

            Info(Localization.Get("LOCSaveVaultBackupAllDone", "Library backup finished.") + "\n" +
                 Localization.Get("LOCSaveVaultStatCreated", "New snapshots:") + " " + created + "\n" +
                 Localization.Get("LOCSaveVaultStatUnchanged", "Unchanged:") + " " + unchanged + "\n" +
                 Localization.Get("LOCSaveVaultStatNoTargets", "No known location:") + " " + empty +
                 (oversized > 0 ? "\n" + Localization.Get("LOCSaveVaultStatOversized", "Too large:") + " " + oversized : string.Empty) +
                 (failed > 0 ? "\n" + Localization.Get("LOCSaveVaultStatFailed", "Failed:") + " " + failed : string.Empty) +
                 QuotaHint());
        }

        /// <summary>
        /// Backup driven by the scheduler. Quiet by design: it reports through a notification
        /// only when something was actually written, and only if the user asked for that.
        /// </summary>
        public void BackupScheduled()
        {
            var games = api.Database.Games.Where(g => !g.Hidden).ToList();
            var created = 0;

            var oversized = new List<string>();

            foreach (var game in games)
            {
                var profile = store.Find(game.Id);
                if (profile == null || !profile.HasTargets || profile.Excluded)
                {
                    continue;
                }

                var result = Backup(game, SnapshotTrigger.Scheduled, false);
                if (result.Created)
                {
                    created++;
                }
                else if (result.TooLarge)
                {
                    oversized.Add(profile.Name);
                }
            }

            // A skipped game is actionable, so it is reported even when backup notifications are
            // off: silently never backing something up is exactly the failure this plugin exists
            // to prevent.
            if (oversized.Count > 0)
            {
                Notify("SaveVaultOversized",
                    Localization.Fill("LOCSaveVaultOversizedNotice",
                        "{0} games were skipped because their save location is over the per snapshot limit: {1}",
                        oversized.Count, string.Join(", ", oversized.Take(5))),
                    NotificationType.Error);
            }

            settings.LastScheduledRunUtc = DateTime.UtcNow;

            if (created > 0 && settings.NotifyOnBackup)
            {
                Notify("SaveVaultScheduled",
                    Localization.Get("LOCSaveVaultScheduledDone", "Scheduled backup:") + " " + created + " " +
                    Localization.Get("LOCSaveVaultSnapshotsWord", "snapshots"));
            }
        }

        // ---------------------------------------------------------------------- restore

        /// <summary>
        /// Restores a snapshot after confirmation. A safety snapshot of the current state is
        /// always taken first, so an accidental restore is one more restore away from being
        /// undone.
        /// </summary>
        public void RestoreInteractive(Game game, GameSaveProfile profile, SnapshotRecord snapshot)
        {
            if (profile == null || snapshot == null)
            {
                return;
            }

            if (settings.ConfirmBeforeRestore)
            {
                var question = Localization.Get("LOCSaveVaultRestoreConfirm",
                                   "Overwrite the current saves with this snapshot?") + "\n\n" +
                               profile.Name + "\n" + snapshot.Id + "  ·  " + FormatSize(snapshot.Bytes) + "\n\n" +
                               Localization.Get("LOCSaveVaultRestoreUndoHint",
                                   "The current state is saved as a snapshot first, so this can be undone.");

                if (api.Dialogs.ShowMessage(question, Name(), System.Windows.MessageBoxButton.YesNo) !=
                    System.Windows.MessageBoxResult.Yes)
                {
                    return;
                }
            }

            RestoreResult result = null;
            RunWithProgress(Localization.Get("LOCSaveVaultProgressRestore", "Restoring") + " - " + profile.Name,
                () =>
                {
                    lock (workGate)
                    {
                        result = snapshots.Restore(game, profile, snapshot);
                        store.Save();
                    }
                });

            RaiseChanged();

            if (result == null)
            {
                return;
            }

            if (result.Failed)
            {
                Error(Localization.Get("LOCSaveVaultRestoreFailed", "Restore failed.") + "\n" + result.Error);
                return;
            }

            Info(Localization.Get("LOCSaveVaultRestoreDone", "Restore finished.") + "\n" +
                 result.Files + " " + Localization.Get("LOCSaveVaultFilesWord", "files") +
                 (result.RegistryKeys > 0
                     ? "  ·  " + result.RegistryKeys + " " + Localization.Get("LOCSaveVaultRegistryWord", "registry keys")
                     : string.Empty) +
                 (string.IsNullOrEmpty(result.UndoSnapshotId)
                     ? string.Empty
                     : "\n" + Localization.Get("LOCSaveVaultUndoSnapshot", "Undo snapshot:") + " " + result.UndoSnapshotId));
        }

        // ------------------------------------------------------------------ maintenance

        /// <summary>Deletes a snapshot after confirmation.</summary>
        public bool DeleteInteractive(GameSaveProfile profile, SnapshotRecord snapshot)
        {
            if (profile == null || snapshot == null)
            {
                return false;
            }

            var question = Localization.Get("LOCSaveVaultDeleteConfirm", "Delete this snapshot?") + "\n\n" +
                           snapshot.Id + "  ·  " + FormatSize(snapshot.Bytes);

            if (api.Dialogs.ShowMessage(question, Name(), System.Windows.MessageBoxButton.YesNo) !=
                System.Windows.MessageBoxResult.Yes)
            {
                return false;
            }

            lock (workGate)
            {
                snapshots.Delete(profile, snapshot);
                store.Save();
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Toggles the pin that protects a snapshot from every retention rule.</summary>
        public void TogglePin(GameSaveProfile profile, SnapshotRecord snapshot)
        {
            if (profile == null || snapshot == null)
            {
                return;
            }

            snapshot.Pinned = !snapshot.Pinned;
            store.Save();
            RaiseChanged();
        }

        /// <summary>Applies the retention rules and the size budget to the whole vault.</summary>
        public void PruneInteractive()
        {
            var before = store.TotalBytes();

            RunWithProgress(Localization.Get("LOCSaveVaultProgressPrune", "Applying retention rules"),
                () =>
                {
                    lock (workGate)
                    {
                        foreach (var profile in store.Profiles())
                        {
                            snapshots.Prune(profile);
                        }

                        snapshots.EnforceQuota();
                        store.Save();
                    }
                });

            RaiseChanged();

            var after = store.TotalBytes();
            Info(Localization.Get("LOCSaveVaultPruneDone", "Retention applied.") + "\n" +
                 Localization.Get("LOCSaveVaultPruneFreed", "Reclaimed:") + " " + FormatSize(Math.Max(0, before - after)) + "\n" +
                 Localization.Get("LOCSaveVaultVaultSize", "Vault size:") + " " + FormatSize(after) +
                 QuotaHint());
        }

        /// <summary>
        /// Re-checks every stored "learned during play" location against the current
        /// plausibility rules and drops the ones that cannot belong to their game.
        ///
        /// Only learned folder targets are examined. Manual entries are the user's word and are
        /// never touched, and the scanner based origins are re-derived by a normal detection run
        /// anyway. A profile whose game is no longer in the library is skipped rather than
        /// cleaned: without the game there is no title to match against, and guessing would
        /// delete the very records that are hardest to recreate.
        /// </summary>
        /// <param name="dropped">
        /// Optional sink that receives every removed target as game id and path, so a caller can
        /// work out which snapshots were built from data that is no longer trusted.
        /// </param>
        /// <returns>Number of targets removed.</returns>
        public int PurgeImplausibleLearned(List<KeyValuePair<Guid, string>> dropped = null)
        {
            var removed = 0;

            lock (workGate)
            {
                foreach (var profile in store.Profiles())
                {
                    if (profile == null || profile.Targets == null || profile.Targets.Count == 0)
                    {
                        continue;
                    }

                    // Nested duplicates are removable without knowing anything about the game.
                    var nested = profile.Targets
                        .Where(t => t != null && t.Kind == TargetKind.Folder)
                        .Select(t => t.Path)
                        .ToList();

                    if (SaveScanner.Collapse(profile) > 0)
                    {
                        foreach (var path in nested.Where(x => !profile.Targets.Any(t => t.Path == x)))
                        {
                            removed++;
                            if (dropped != null)
                            {
                                dropped.Add(new KeyValuePair<Guid, string>(profile.GameId, path));
                            }
                        }
                    }

                    var suspects = profile.Targets
                        .Where(t => t != null && t.Origin == TargetOrigin.Learned && t.Kind == TargetKind.Folder)
                        .ToList();

                    if (suspects.Count == 0)
                    {
                        continue;
                    }

                    var game = api == null || api.Database == null ? null : api.Database.Games.Get(profile.GameId);
                    if (game == null)
                    {
                        logger.Debug("Save Vault: keeping learned locations of " + profile.Name + ", the game is gone from the library.");
                        continue;
                    }

                    var context = LearnContext.For(game, settings);

                    foreach (var target in suspects)
                    {
                        string reason;
                        if (LearnedGuard.Plausible(target.Path, context, out reason))
                        {
                            continue;
                        }

                        profile.Targets.Remove(target);
                        removed++;

                        if (dropped != null)
                        {
                            dropped.Add(new KeyValuePair<Guid, string>(profile.GameId, target.Path));
                        }

                        logger.Info("Save Vault: dropped learned location " + target.Path + " from " + profile.Name + " (" + reason + ").");
                    }
                }

                if (removed > 0)
                {
                    store.Save();
                }
            }

            if (removed > 0)
            {
                RaiseChanged();
            }

            return removed;
        }

        /// <summary>
        /// Menu entry for <see cref="PurgeImplausibleLearned"/>: purges the bad locations, then
        /// offers to delete the snapshots that were built from them.
        ///
        /// The two halves are deliberately separate decisions. A target is metadata and can be
        /// found again by another scan, so it is removed without asking; a snapshot is the only
        /// copy of something and is never deleted without a yes.
        /// </summary>
        public void PurgeLearnedInteractive()
        {
            var removed = 0;
            var dropped = new List<KeyValuePair<Guid, string>>();

            RunWithProgress(Localization.Get("LOCSaveVaultProgressPurgeLearned", "Re-checking learned locations"),
                () => { removed = PurgeImplausibleLearned(dropped); });

            var message = Localization.Get("LOCSaveVaultPurgeDone", "Check complete.") + "\n" +
                          Localization.Get("LOCSaveVaultPurgeRemoved", "Removed:") + " " + removed;

            var tainted = Tainted();
            if (tainted.Count == 0)
            {
                Info(message + QuotaHint());
                return;
            }

            var bytes = tainted.Sum(t => t.Item2.Bytes);
            var onlyCopy = tainted.Count(t => t.Item1.Snapshots.Count == 1);

            var ask = message + "\n\n" +
                      Localization.Fill("LOCSaveVaultPurgeTainted",
                          "{0} snapshots contain data copied from those locations, taking {1}. Delete them?",
                          tainted.Count, FormatSize(bytes)) +
                      (onlyCopy == 0
                          ? string.Empty
                          : "\n" + Localization.Fill("LOCSaveVaultPurgeTaintedOnly",
                                "{0} of them are the only snapshot their game has. A fresh backup is taken right after, using the locations that are left.",
                                onlyCopy));

            if (api == null || api.Dialogs == null ||
                api.Dialogs.ShowMessage(ask, Name(), System.Windows.MessageBoxButton.YesNo) !=
                    System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            var freed = 0L;
            var again = new List<Guid>();

            RunWithProgress(Localization.Get("LOCSaveVaultProgressPurgeLearned", "Re-checking learned locations"),
                () =>
                {
                    lock (workGate)
                    {
                        foreach (var item in tainted)
                        {
                            var size = item.Item2.Bytes;
                            if (!snapshots.Delete(item.Item1, item.Item2))
                            {
                                continue;
                            }

                            freed += size;

                            if (item.Item1.HasTargets && !again.Contains(item.Item1.GameId))
                            {
                                again.Add(item.Item1.GameId);
                            }
                        }

                        store.Save();
                    }
                });

            // A clean snapshot immediately afterwards, so a game does not sit unprotected just
            // because its only backup happened to be junk.
            foreach (var id in again)
            {
                var game = api.Database.Games.Get(id);
                if (game != null)
                {
                    Backup(game, SnapshotTrigger.Manual, true);
                }
            }

            RaiseChanged();

            Info(Localization.Get("LOCSaveVaultPurgeDone", "Check complete.") + "\n" +
                 Localization.Get("LOCSaveVaultPruneFreed", "Reclaimed:") + " " + FormatSize(freed) + "\n" +
                 Localization.Get("LOCSaveVaultVaultSize", "Vault size:") + " " + FormatSize(store.TotalBytes()) +
                 QuotaHint());
        }

        /// <summary>
        /// Snapshots whose sources include a learned path that no longer passes the rules.
        ///
        /// Deliberately independent of the purge that just ran. The silent check at startup
        /// removes the bad locations on its own, so by the time somebody opens the menu entry
        /// there is nothing left to drop, while the oversized snapshots those locations produced
        /// are still on disk. Reading the snapshot's own recorded sources finds them either way.
        /// </summary>
        private List<Tuple<GameSaveProfile, SnapshotRecord>> Tainted()
        {
            var result = new List<Tuple<GameSaveProfile, SnapshotRecord>>();

            foreach (var profile in store.Profiles())
            {
                if (profile == null || profile.Snapshots == null || profile.Snapshots.Count == 0)
                {
                    continue;
                }

                var game = api == null || api.Database == null ? null : api.Database.Games.Get(profile.GameId);
                if (game == null)
                {
                    // No game means no way to tell a save folder from a stranger, so keep the data.
                    continue;
                }

                var context = LearnContext.For(game, settings);
                var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (profile.Targets != null)
                {
                    foreach (var target in profile.Targets)
                    {
                        if (target != null && target.Kind == TargetKind.Folder)
                        {
                            kept.Add(SaveTarget.Normalize(target.Path));
                        }
                    }
                }

                foreach (var snapshot in profile.Snapshots.ToList())
                {
                    if (snapshot.Sources == null || snapshot.Sources.Count == 0)
                    {
                        continue;
                    }

                    foreach (var source in snapshot.Sources)
                    {
                        if (source == null || source.Kind != TargetKind.Folder ||
                            source.Origin != TargetOrigin.Learned ||
                            string.IsNullOrWhiteSpace(source.Path) ||
                            kept.Contains(SaveTarget.Normalize(source.Path)))
                        {
                            continue;
                        }

                        string reason;
                        if (LearnedGuard.Plausible(source.Path, context, out reason))
                        {
                            continue;
                        }

                        result.Add(Tuple.Create(profile, snapshot));
                        break;
                    }
                }
            }

            return result;
        }

        public void OpenVaultFolder(GameSaveProfile profile)
        {
            try
            {
                var path = profile == null ? store.Root : store.ProfileFolder(profile);
                if (!Directory.Exists(path))
                {
                    path = store.Root;
                }

                if (!Directory.Exists(path))
                {
                    store.EnsureRoot();
                }

                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: could not open the vault folder.");
            }
        }

        public void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                if (Directory.Exists(path))
                {
                    Process.Start("explorer.exe", "\"" + path + "\"");
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: could not open " + path);
            }
        }

        // ---------------------------------------------------------------------- helpers

        /// <summary>
        /// Extra line for a summary dialog when the vault is over its size budget.
        ///
        /// Retention refuses to delete the only snapshot a game owns, so a library where many
        /// games hold one large snapshot each stays over the limit no matter how often pruning
        /// runs. Naming the biggest games turns a silent overrun into something the user can fix.
        /// </summary>
        private string QuotaHint()
        {
            var overflow = snapshots.Overflow();
            if (overflow <= 0)
            {
                return string.Empty;
            }

            var biggest = snapshots.Largest(3)
                .Select(p => p.Key + " (" + FormatSize(p.Value) + ")")
                .ToList();

            return "\n\n" + Localization.Fill("LOCSaveVaultQuotaOver",
                       "The vault is {0} over the {1} budget. Retention keeps the only snapshot of every game, so the rest has to go by hand.",
                       FormatSize(overflow),
                       FormatSize((long)settings.MaxTotalMegabytes * 1024L * 1024L)) +
                   (biggest.Count == 0
                       ? string.Empty
                       : "\n" + Localization.Get("LOCSaveVaultQuotaBiggest", "Largest:") + " " + string.Join(", ", biggest));
        }

        /// <summary>Human readable byte count, used everywhere a size is shown.</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 MB";
            }

            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return (unit <= 1 ? value.ToString("0") : value.ToString("0.0")) + " " + units[unit];
        }

        public static string Name()
        {
            return Localization.Get("LOCSaveVaultName", "Save Vault");
        }

        private void RunWithProgress(string caption, Action body)
        {
            RunWithProgress(caption, progress => body());
        }

        private void RunWithProgress(string caption, Action<GlobalProgressActionArgs> body)
        {
            if (api == null || api.Dialogs == null)
            {
                body(null);
                return;
            }

            try
            {
                api.Dialogs.ActivateGlobalProgress(progress => body(progress),
                    new GlobalProgressOptions(caption, true) { IsIndeterminate = false });
            }
            catch (Exception e)
            {
                logger.Warn(e, "Save Vault: falling back to a foreground run without progress.");
                body(null);
            }
        }

        private void Info(string message)
        {
            if (api == null || api.Dialogs == null)
            {
                return;
            }

            api.Dialogs.ShowMessage(message, Name());
        }

        private void Error(string message)
        {
            if (api == null || api.Dialogs == null)
            {
                return;
            }

            api.Dialogs.ShowErrorMessage(message, Name());
        }

        public void Notify(string id, string message, NotificationType type = NotificationType.Info)
        {
            if (api == null || api.Notifications == null)
            {
                return;
            }

            api.Notifications.Add(new NotificationMessage(id + Guid.NewGuid().ToString("N").Substring(0, 4), message, type));
        }
    }
}
