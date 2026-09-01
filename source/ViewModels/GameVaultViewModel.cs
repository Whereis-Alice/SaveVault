using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using SaveVault.Models;
using SaveVault.Services;

namespace SaveVault.ViewModels
{
    /// <summary>How well protected the selected game is, used to colour the status dot.</summary>
    public enum VaultStatus
    {
        None = 0,
        Unknown = 1,
        Pending = 2,
        Protected = 3
    }

    /// <summary>
    /// Backs the panel a theme mounts in the details view.
    ///
    /// Exactly one instance exists for the whole plugin, so a theme that mounts the panel in the
    /// details view and again in the grid details pane still shows one consistent state, and a
    /// backup started from the game menu updates both without either control knowing about it.
    /// </summary>
    public class GameVaultViewModel : ObservableObject
    {
        /// <summary>How many snapshots the compact panel lists before it defers to the manager.</summary>
        private const int PanelSnapshotLimit = 5;

        private readonly VaultController controller;
        private readonly Action<Game> openManager;

        private Game game;
        private GameSaveProfile profile;
        private bool targetsExpanded;

        public GameVaultViewModel(VaultController controller, Action<Game> openManager)
        {
            this.controller = controller;
            this.openManager = openManager;

            Snapshots = new ObservableCollection<SnapshotViewModel>();
            Targets = new ObservableCollection<TargetViewModel>();

            BackupCommand = new RelayCommand(Backup, () => game != null);
            DetectCommand = new RelayCommand(Detect, () => game != null);
            ManageCommand = new RelayCommand(Manage);
            OpenFolderCommand = new RelayCommand(OpenFolder, () => profile != null);
            ToggleTargetsCommand = new RelayCommand(() => TargetsExpanded = !TargetsExpanded);
            RestoreCommand = new RelayCommand<SnapshotViewModel>(Restore, item => item != null);
            OpenPathCommand = new RelayCommand<TargetViewModel>(OpenPath, item => item != null);

            controller.Changed += OnControllerChanged;
        }

        // ------------------------------------------------------------------- collections

        public ObservableCollection<SnapshotViewModel> Snapshots { get; private set; }

        public ObservableCollection<TargetViewModel> Targets { get; private set; }

        // ------------------------------------------------------------------- commands

        public RelayCommand BackupCommand { get; private set; }

        public RelayCommand DetectCommand { get; private set; }

        public RelayCommand ManageCommand { get; private set; }

        public RelayCommand OpenFolderCommand { get; private set; }

        public RelayCommand ToggleTargetsCommand { get; private set; }

        public RelayCommand<SnapshotViewModel> RestoreCommand { get; private set; }

        public RelayCommand<TargetViewModel> OpenPathCommand { get; private set; }

        // ------------------------------------------------------------------- state

        public Game CurrentGame
        {
            get { return game; }
        }

        public bool HasGame
        {
            get { return game != null; }
        }

        public VaultStatus Status
        {
            get
            {
                if (game == null)
                {
                    return VaultStatus.None;
                }

                if (profile == null || !profile.HasTargets)
                {
                    return VaultStatus.Unknown;
                }

                return profile.Snapshots.Count > 0 ? VaultStatus.Protected : VaultStatus.Pending;
            }
        }

        /// <summary>One line that answers "is this game safe" without any clicking.</summary>
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case VaultStatus.Protected:
                        return VaultText.Fill("LOCSaveVaultStatusProtected", "{0} snapshots · last {1}",
                            profile.Snapshots.Count, VaultText.Ago(profile.LastBackupUtc));
                    case VaultStatus.Pending:
                        return VaultText.Fill("LOCSaveVaultStatusPending", "{0} locations found, no snapshot yet",
                            profile.Targets.Count(target => target.Enabled));
                    case VaultStatus.Unknown:
                        return Localization.Get("LOCSaveVaultStatusUnknown", "No save location found yet.");
                    default:
                        return Localization.Get("LOCSaveVaultStatusNoGame", "No game selected.");
                }
            }
        }

        /// <summary>Second line: where the data lives and how much of it there is.</summary>
        public string DetailText
        {
            get
            {
                if (profile == null)
                {
                    return null;
                }

                var parts = new List<string>();
                var enabled = profile.Targets.Count(target => target.Enabled);
                if (enabled > 0)
                {
                    parts.Add(VaultText.Fill("LOCSaveVaultLocationsCount", "{0} locations", enabled));
                }

                var bytes = profile.Snapshots.Sum(snapshot => snapshot.Bytes);
                if (bytes > 0)
                {
                    parts.Add(VaultController.FormatSize(bytes));
                }

                if (profile.Excluded)
                {
                    parts.Add(Localization.Get("LOCSaveVaultExcludedMark", "excluded"));
                }

                return parts.Count == 0 ? null : string.Join("  ·  ", parts);
            }
        }

        public bool HasSnapshots
        {
            get { return Snapshots.Count > 0; }
        }

        public bool HasTargets
        {
            get { return Targets.Count > 0; }
        }

        /// <summary>Collapsed by default: the paths matter when something looks wrong, not before.</summary>
        public bool TargetsExpanded
        {
            get { return targetsExpanded; }
            set { SetValue(ref targetsExpanded, value); }
        }

        /// <summary>Shown only when the panel is hiding older snapshots.</summary>
        public string MoreText
        {
            get
            {
                if (profile == null || profile.Snapshots.Count <= PanelSnapshotLimit)
                {
                    return null;
                }

                return VaultText.Fill("LOCSaveVaultShowAll", "Show all {0} snapshots", profile.Snapshots.Count);
            }
        }

        public bool IsExcluded
        {
            get { return profile != null && profile.Excluded; }
            set
            {
                if (game == null)
                {
                    return;
                }

                var current = controller.Store.GetOrCreate(game);
                if (current.Excluded == value)
                {
                    return;
                }

                current.Excluded = value;
                controller.Store.Save();
                Reload();
            }
        }

        // ------------------------------------------------------------------- plumbing

        /// <summary>
        /// Called by every mounted control on game context change, and by the plugin when the
        /// selection changes while no control is mounted.
        /// </summary>
        public void SetGame(Game value)
        {
            game = value;
            Reload();
        }

        /// <summary>Rebuilds the projection from the stored profile. Cheap: no disk scan, no hashing.</summary>
        public void Reload()
        {
            profile = game == null ? null : controller.Store.Find(game.Id);

            Snapshots.Clear();
            Targets.Clear();

            if (profile != null)
            {
                foreach (var snapshot in profile.Snapshots
                    .OrderByDescending(item => item.CreatedUtc)
                    .Take(PanelSnapshotLimit))
                {
                    Snapshots.Add(new SnapshotViewModel(profile, snapshot, TogglePin));
                }

                foreach (var target in profile.Targets
                    .OrderBy(item => item.Origin)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Targets.Add(new TargetViewModel(target, Persist));
                }
            }

            OnPropertyChanged("CurrentGame");
            OnPropertyChanged("HasGame");
            OnPropertyChanged("Status");
            OnPropertyChanged("StatusText");
            OnPropertyChanged("DetailText");
            OnPropertyChanged("HasSnapshots");
            OnPropertyChanged("HasTargets");
            OnPropertyChanged("MoreText");
            OnPropertyChanged("IsExcluded");
        }

        private void OnControllerChanged(object sender, EventArgs e)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.CheckAccess())
            {
                Reload();
                return;
            }

            // Backups run on the progress dialog's worker thread; touching the collections from
            // there would throw the moment a control is bound to them.
            app.Dispatcher.BeginInvoke(new Action(Reload));
        }

        private void Persist()
        {
            controller.Store.Save();
            Reload();
        }

        private void TogglePin(SnapshotViewModel item)
        {
            controller.TogglePin(item.Profile, item.Snapshot);
        }

        // ------------------------------------------------------------------- actions

        private void Backup()
        {
            if (game != null)
            {
                controller.BackupInteractive(game);
            }
        }

        private void Detect()
        {
            if (game != null)
            {
                controller.DetectInteractive(game);
            }
        }

        private void Manage()
        {
            if (openManager != null)
            {
                openManager(game);
            }
        }

        private void OpenFolder()
        {
            if (profile != null)
            {
                controller.OpenVaultFolder(profile);
            }
        }

        private void Restore(SnapshotViewModel item)
        {
            if (game == null || item == null)
            {
                return;
            }

            controller.RestoreInteractive(game, item.Profile, item.Snapshot);
        }

        private void OpenPath(TargetViewModel item)
        {
            if (item != null && !item.IsRegistry)
            {
                controller.OpenPath(item.PathText);
            }
        }
    }
}
