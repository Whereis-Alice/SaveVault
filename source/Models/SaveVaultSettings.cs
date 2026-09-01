using System;
using System.Collections.Generic;
using Playnite.SDK.Data;

namespace SaveVault.Models
{
    /// <summary>
    /// Persisted plugin settings. Everything the user can influence lives here so that a
    /// single Serialization.GetClone in the settings view model is enough to implement
    /// cancel, and so that defaults live in exactly one place.
    /// </summary>
    public class SaveVaultSettings : ObservableObject
    {
        private string backupRoot = DefaultBackupRoot;
        private int maxTotalMegabytes = 2048;
        private int maxSnapshotsPerGame = 10;
        private int keepDaily = 7;
        private int keepWeekly = 4;

        private bool backupOnGameStopped = true;
        private bool backupBeforeLaunch;
        private bool scheduledBackupEnabled = true;
        private int scheduleIntervalMinutes = 360;
        private bool compressSnapshots = true;
        private bool confirmBeforeRestore = true;
        private bool notifyOnBackup = true;

        private bool includeRegistry;
        private bool runtimeLearning = true;
        private bool useLudusaviManifest = true;
        private string ludusaviManifestPath = string.Empty;
        private bool scanInstallDir = true;
        private bool scanUserFolders = true;
        private int maxCandidateMegabytes = 256;
        private int maxCandidateFiles = 5000;
        private bool autoScanNewGames = true;

        private DateTime? lastScheduledRunUtc;

        /// <summary>
        /// Default vault location. Deliberately not under the user profile: save archives
        /// grow, and the system drive on this machine is small.
        /// </summary>
        public const string DefaultBackupRoot = @"D:\GAMES\存档备份汇总";

        [SerializationPropertyName("backupRoot")]
        public string BackupRoot
        {
            get { return backupRoot; }
            set { SetValue(ref backupRoot, value); }
        }

        /// <summary>Total size budget for the whole vault. Pruning keeps the vault below it.</summary>
        [SerializationPropertyName("maxTotalMegabytes")]
        public int MaxTotalMegabytes
        {
            get { return maxTotalMegabytes; }
            set { SetValue(ref maxTotalMegabytes, value); }
        }

        [SerializationPropertyName("maxSnapshotsPerGame")]
        public int MaxSnapshotsPerGame
        {
            get { return maxSnapshotsPerGame; }
            set { SetValue(ref maxSnapshotsPerGame, value); }
        }

        /// <summary>Days for which the newest snapshot of each day is protected from pruning.</summary>
        [SerializationPropertyName("keepDaily")]
        public int KeepDaily
        {
            get { return keepDaily; }
            set { SetValue(ref keepDaily, value); }
        }

        /// <summary>Weeks for which the newest snapshot of each week is protected from pruning.</summary>
        [SerializationPropertyName("keepWeekly")]
        public int KeepWeekly
        {
            get { return keepWeekly; }
            set { SetValue(ref keepWeekly, value); }
        }

        [SerializationPropertyName("backupOnGameStopped")]
        public bool BackupOnGameStopped
        {
            get { return backupOnGameStopped; }
            set { SetValue(ref backupOnGameStopped, value); }
        }

        [SerializationPropertyName("backupBeforeLaunch")]
        public bool BackupBeforeLaunch
        {
            get { return backupBeforeLaunch; }
            set { SetValue(ref backupBeforeLaunch, value); }
        }

        [SerializationPropertyName("scheduledBackupEnabled")]
        public bool ScheduledBackupEnabled
        {
            get { return scheduledBackupEnabled; }
            set { SetValue(ref scheduledBackupEnabled, value); }
        }

        [SerializationPropertyName("scheduleIntervalMinutes")]
        public int ScheduleIntervalMinutes
        {
            get { return scheduleIntervalMinutes; }
            set { SetValue(ref scheduleIntervalMinutes, value); }
        }

        /// <summary>Snapshots are always zip archives; this only picks the compression level
        /// (Optimal when on, NoCompression when off, for very large or already-compressed saves).</summary>
        [SerializationPropertyName("compressSnapshots")]
        public bool CompressSnapshots
        {
            get { return compressSnapshots; }
            set { SetValue(ref compressSnapshots, value); }
        }

        [SerializationPropertyName("confirmBeforeRestore")]
        public bool ConfirmBeforeRestore
        {
            get { return confirmBeforeRestore; }
            set { SetValue(ref confirmBeforeRestore, value); }
        }

        /// <summary>Toast after automatic backups. Manual actions always report.</summary>
        [SerializationPropertyName("notifyOnBackup")]
        public bool NotifyOnBackup
        {
            get { return notifyOnBackup; }
            set { SetValue(ref notifyOnBackup, value); }
        }

        /// <summary>
        /// Registry saves are exported with reg.exe. Off by default: it is the noisiest
        /// detection layer and the least likely to matter for a visual novel library.
        /// </summary>
        [SerializationPropertyName("includeRegistry")]
        public bool IncludeRegistry
        {
            get { return includeRegistry; }
            set { SetValue(ref includeRegistry, value); }
        }

        /// <summary>
        /// Observe which folders a game writes to while it runs. This is what finds saves
        /// that no database knows about, which is most of a Japanese doujin library.
        /// </summary>
        [SerializationPropertyName("runtimeLearning")]
        public bool RuntimeLearning
        {
            get { return runtimeLearning; }
            set { SetValue(ref runtimeLearning, value); }
        }

        [SerializationPropertyName("useLudusaviManifest")]
        public bool UseLudusaviManifest
        {
            get { return useLudusaviManifest; }
            set { SetValue(ref useLudusaviManifest, value); }
        }

        /// <summary>Empty means the standard %APPDATA%\ludusavi\manifest.yaml location.</summary>
        [SerializationPropertyName("ludusaviManifestPath")]
        public string LudusaviManifestPath
        {
            get { return ludusaviManifestPath; }
            set { SetValue(ref ludusaviManifestPath, value); }
        }

        [SerializationPropertyName("scanInstallDir")]
        public bool ScanInstallDir
        {
            get { return scanInstallDir; }
            set { SetValue(ref scanInstallDir, value); }
        }

        [SerializationPropertyName("scanUserFolders")]
        public bool ScanUserFolders
        {
            get { return scanUserFolders; }
            set { SetValue(ref scanUserFolders, value); }
        }

        /// <summary>A candidate bigger than this is treated as game data, not a save.</summary>
        [SerializationPropertyName("maxCandidateMegabytes")]
        public int MaxCandidateMegabytes
        {
            get { return maxCandidateMegabytes; }
            set { SetValue(ref maxCandidateMegabytes, value); }
        }

        [SerializationPropertyName("maxCandidateFiles")]
        public int MaxCandidateFiles
        {
            get { return maxCandidateFiles; }
            set { SetValue(ref maxCandidateFiles, value); }
        }

        /// <summary>Detect targets the first time a game is played or backed up.</summary>
        [SerializationPropertyName("autoScanNewGames")]
        public bool AutoScanNewGames
        {
            get { return autoScanNewGames; }
            set { SetValue(ref autoScanNewGames, value); }
        }

        [SerializationPropertyName("lastScheduledRunUtc")]
        public DateTime? LastScheduledRunUtc
        {
            get { return lastScheduledRunUtc; }
            set { SetValue(ref lastScheduledRunUtc, value); }
        }

        /// <summary>Effective vault root, falling back to the default when cleared.</summary>
        [DontSerialize]
        public string EffectiveBackupRoot
        {
            get { return string.IsNullOrWhiteSpace(BackupRoot) ? DefaultBackupRoot : BackupRoot.Trim(); }
        }

        /// <summary>
        /// Copies every value in place. Used by the settings view model so that cancel can
        /// restore the pre-edit state without replacing the instance the plugin holds.
        /// </summary>
        public void CopyFrom(SaveVaultSettings other)
        {
            if (other == null)
            {
                return;
            }

            BackupRoot = other.BackupRoot;
            MaxTotalMegabytes = other.MaxTotalMegabytes;
            MaxSnapshotsPerGame = other.MaxSnapshotsPerGame;
            KeepDaily = other.KeepDaily;
            KeepWeekly = other.KeepWeekly;

            BackupOnGameStopped = other.BackupOnGameStopped;
            BackupBeforeLaunch = other.BackupBeforeLaunch;
            ScheduledBackupEnabled = other.ScheduledBackupEnabled;
            ScheduleIntervalMinutes = other.ScheduleIntervalMinutes;
            CompressSnapshots = other.CompressSnapshots;
            ConfirmBeforeRestore = other.ConfirmBeforeRestore;
            NotifyOnBackup = other.NotifyOnBackup;

            IncludeRegistry = other.IncludeRegistry;
            RuntimeLearning = other.RuntimeLearning;
            UseLudusaviManifest = other.UseLudusaviManifest;
            LudusaviManifestPath = other.LudusaviManifestPath;
            ScanInstallDir = other.ScanInstallDir;
            ScanUserFolders = other.ScanUserFolders;
            MaxCandidateMegabytes = other.MaxCandidateMegabytes;
            MaxCandidateFiles = other.MaxCandidateFiles;
            AutoScanNewGames = other.AutoScanNewGames;

            LastScheduledRunUtc = other.LastScheduledRunUtc;
        }
    }
}
