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
                    }
                });

            Info(Localization.Get("LOCSaveVaultBackupAllDone", "Library backup finished.") + "\n" +
                 Localization.Get("LOCSaveVaultStatCreated", "New snapshots:") + " " + created + "\n" +
                 Localization.Get("LOCSaveVaultStatUnchanged", "Unchanged:") + " " + unchanged + "\n" +
                 Localization.Get("LOCSaveVaultStatNoTargets", "No known location:") + " " + empty +
                 (failed > 0 ? "\n" + Localization.Get("LOCSaveVaultStatFailed", "Failed:") + " " + failed : string.Empty));
        }

        /// <summary>
        /// Backup driven by the scheduler. Quiet by design: it reports through a notification
        /// only when something was actually written, and only if the user asked for that.
        /// </summary>
        public void BackupScheduled()
        {
            var games = api.Database.Games.Where(g => !g.Hidden).ToList();
            var created = 0;

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
                 Localization.Get("LOCSaveVaultVaultSize", "Vault size:") + " " + FormatSize(after));
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
