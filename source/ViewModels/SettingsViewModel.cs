using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Data;
using SaveVault.Models;
using SaveVault.Services;

namespace SaveVault.ViewModels
{
    /// <summary>
    /// Backs the add-on settings page.
    ///
    /// Editing works against the live settings instance so a changed value takes effect the moment
    /// it is typed; the clone taken in <see cref="BeginEdit" /> is what lets Cancel restore every
    /// value without swapping the object the rest of the plugin holds a reference to.
    /// </summary>
    public class SettingsViewModel : ObservableObject, ISettings
    {
        private readonly SaveVaultSettings settings;
        private readonly VaultController controller;
        private readonly IPlayniteAPI api;
        private readonly Action save;

        private SaveVaultSettings snapshot;
        private string vaultStats;

        public SettingsViewModel(SaveVaultSettings settings, VaultController controller, IPlayniteAPI api, Action save)
        {
            this.settings = settings;
            this.controller = controller;
            this.api = api;
            this.save = save;

            ThemeKeys = new ObservableCollection<string>(ThemeBridge.PublishedKeys());

            PickRootCommand = new RelayCommand(PickRoot);
            PickManifestCommand = new RelayCommand(PickManifest);
            ResetRootCommand = new RelayCommand(() => settings.BackupRoot = SaveVaultSettings.DefaultBackupRoot);
            OpenRootCommand = new RelayCommand(() => controller.OpenPath(controller.Store.Root));
            RefreshStatsCommand = new RelayCommand(() => VaultStats = ReadStats());
            PruneCommand = new RelayCommand(Prune);
            ReloadManifestCommand = new RelayCommand(ReloadManifest);

            vaultStats = ReadStats();
        }

        public SaveVaultSettings Settings
        {
            get { return settings; }
        }

        /// <summary>Every resource key a theme may override, listed on the about tab.</summary>
        public ObservableCollection<string> ThemeKeys { get; private set; }

        public RelayCommand PickRootCommand { get; private set; }

        public RelayCommand PickManifestCommand { get; private set; }

        public RelayCommand ResetRootCommand { get; private set; }

        public RelayCommand OpenRootCommand { get; private set; }

        public RelayCommand RefreshStatsCommand { get; private set; }

        public RelayCommand PruneCommand { get; private set; }

        public RelayCommand ReloadManifestCommand { get; private set; }

        public string VaultStats
        {
            get { return vaultStats; }
            private set { SetValue(ref vaultStats, value); }
        }

        public string VersionText
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return VaultController.Name() + "  " + (version == null ? "1.0" : version.ToString(3));
            }
        }

        // ------------------------------------------------------------- folder pickers

        private void PickRoot()
        {
            if (api == null || api.Dialogs == null)
            {
                return;
            }

            var folder = api.Dialogs.SelectFolder();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                settings.BackupRoot = folder;
                VaultStats = ReadStats();
            }
        }

        private void PickManifest()
        {
            if (api == null || api.Dialogs == null)
            {
                return;
            }

            var file = api.Dialogs.SelectFile("Ludusavi manifest|manifest.yaml;*.yaml|" +
                Localization.Get("LOCSaveVaultAllFiles", "All files") + "|*.*");
            if (!string.IsNullOrWhiteSpace(file))
            {
                settings.LudusaviManifestPath = file;
            }
        }

        // ------------------------------------------------------------- maintenance

        /// <summary>
        /// Reads the numbers straight off the index rather than caching them: the settings page is
        /// opened rarely and a stale size next to a quota field would be worse than a short pause.
        /// </summary>
        private string ReadStats()
        {
            var games = 0;
            var snapshots = 0;
            long bytes = 0;

            try
            {
                var profiles = controller.Store.Profiles().ToList();
                games = profiles.Count;
                snapshots = profiles.Sum(profile => profile.Snapshots.Count);
                bytes = controller.Store.TotalBytes();
            }
            catch (Exception)
            {
                // A missing or unreadable vault folder simply has nothing to report.
            }

            return VaultText.Fill("LOCSaveVaultStats", "{0} games, {1} snapshots, {2} used.",
                games, snapshots, VaultController.FormatSize(bytes));
        }

        private void Prune()
        {
            controller.PruneInteractive();
            VaultStats = ReadStats();
        }

        private void ReloadManifest()
        {
            controller.ResetManifest();
            controller.EnsureManifest();

            if (api != null && api.Dialogs != null)
            {
                api.Dialogs.ShowMessage(
                    Localization.Get("LOCSaveVaultManifestReloaded", "Ludusavi manifest reloaded."),
                    VaultController.Name());
            }
        }

        // ------------------------------------------------------------- ISettings

        public void BeginEdit()
        {
            snapshot = Serialization.GetClone(settings);
            VaultStats = ReadStats();
        }

        public void EndEdit()
        {
            snapshot = null;
            if (save != null)
            {
                save();
            }

            controller.Store.Invalidate();
        }

        public void CancelEdit()
        {
            if (snapshot != null)
            {
                settings.CopyFrom(snapshot);
                snapshot = null;
            }
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            var root = settings.EffectiveBackupRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                errors.Add(Localization.Get("LOCSaveVaultOptBackupRoot", "Backup folder") + ": ?");
            }
            else
            {
                try
                {
                    // A relative or malformed path would only fail much later, during a backup that
                    // the user is not watching, so it is rejected here instead.
                    var full = Path.GetFullPath(root);
                    if (!Path.IsPathRooted(full))
                    {
                        errors.Add(Localization.Get("LOCSaveVaultErrRootRelative", "The backup folder must be an absolute path."));
                    }
                }
                catch (Exception)
                {
                    errors.Add(Localization.Get("LOCSaveVaultErrRootInvalid", "The backup folder is not a valid path."));
                }
            }

            if (settings.MaxTotalMegabytes < 64 || settings.MaxTotalMegabytes > 1024 * 1024)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptMaxTotal", "Total size limit") + ": 64 - 1048576 MB");
            }

            if (settings.MaxSnapshotsPerGame < 1 || settings.MaxSnapshotsPerGame > 500)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptMaxSnapshots", "Snapshots per game") + ": 1 - 500");
            }

            if (settings.KeepDaily < 0 || settings.KeepDaily > 365)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptKeepDaily", "Keep one per day for") + ": 0 - 365");
            }

            if (settings.KeepWeekly < 0 || settings.KeepWeekly > 260)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptKeepWeekly", "Keep one per week for") + ": 0 - 260");
            }

            if (settings.ScheduleIntervalMinutes < 15 || settings.ScheduleIntervalMinutes > 10080)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptScheduleInterval", "Interval") + ": 15 - 10080");
            }

            if (settings.MaxCandidateMegabytes < 1 || settings.MaxCandidateMegabytes > 8192)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptMaxCandidateSize", "Largest folder to accept") + ": 1 - 8192 MB");
            }

            if (settings.MaxCandidateFiles < 1 || settings.MaxCandidateFiles > 200000)
            {
                errors.Add(Localization.Get("LOCSaveVaultOptMaxCandidateFiles", "Largest folder to accept, in files") + ": 1 - 200000");
            }

            return errors.Count == 0;
        }
    }
}
