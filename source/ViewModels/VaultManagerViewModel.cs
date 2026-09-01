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
    /// <summary>One row in the manager's game list.</summary>
    public class VaultGameViewModel : ObservableObject
    {
        public VaultGameViewModel(GameSaveProfile profile, Game game)
        {
            Profile = profile;
            Game = game;
        }

        public GameSaveProfile Profile { get; private set; }

        /// <summary>Null when the game was removed from the library but its snapshots are still kept.</summary>
        public Game Game { get; private set; }

        public string NameText
        {
            get { return Game != null ? Game.Name : Profile.Name; }
        }

        public long Bytes
        {
            get { return Profile.Snapshots.Sum(snapshot => snapshot.Bytes); }
        }

        public string SizeText
        {
            get { return VaultController.FormatSize(Bytes); }
        }

        public string StatusText
        {
            get
            {
                if (!Profile.HasTargets)
                {
                    return Localization.Get("LOCSaveVaultStatusUnknown", "No save location found yet.");
                }

                if (Profile.Snapshots.Count == 0)
                {
                    return Localization.Get("LOCSaveVaultStatusNoSnapshot", "No snapshot yet");
                }

                return VaultText.Fill("LOCSaveVaultRowStatus", "{0} snapshots · {1}",
                    Profile.Snapshots.Count, VaultText.Ago(Profile.LastBackupUtc));
            }
        }

        public bool IsMissing
        {
            get { return Game == null; }
        }

        public bool IsExcluded
        {
            get { return Profile.Excluded; }
        }

        public void Refresh()
        {
            OnPropertyChanged("NameText");
            OnPropertyChanged("SizeText");
            OnPropertyChanged("StatusText");
            OnPropertyChanged("IsExcluded");
        }
    }

    /// <summary>
    /// Backs the manager window: every game the vault knows about on the left, its targets and
    /// snapshots on the right.
    ///
    /// The details panel is deliberately minimal, so this is where the operations that need room
    /// live - restoring an older snapshot, pinning one so retention cannot delete it, correcting a
    /// wrong path, and seeing what the 2 GB budget is actually being spent on.
    /// </summary>
    public class VaultManagerViewModel : ObservableObject
    {
        private readonly VaultController controller;
        private readonly IPlayniteAPI api;

        private VaultGameViewModel selected;
        private SnapshotViewModel selectedSnapshot;
        private TargetViewModel selectedTarget;
        private string filter;
        private bool suspend;

        public VaultManagerViewModel(VaultController controller, IPlayniteAPI api)
        {
            this.controller = controller;
            this.api = api;

            Games = new ObservableCollection<VaultGameViewModel>();
            Snapshots = new ObservableCollection<SnapshotViewModel>();
            Targets = new ObservableCollection<TargetViewModel>();

            BackupCommand = new RelayCommand(Backup, () => selected != null && selected.Game != null);
            DetectCommand = new RelayCommand(Detect, () => selected != null && selected.Game != null);
            RestoreCommand = new RelayCommand(Restore, () => selectedSnapshot != null && selected != null && selected.Game != null);
            DeleteCommand = new RelayCommand(Delete, () => selectedSnapshot != null);
            OpenFolderCommand = new RelayCommand(OpenFolder, () => selected != null);
            OpenPathCommand = new RelayCommand<TargetViewModel>(OpenPath, item => item != null);
            AddTargetCommand = new RelayCommand(AddTarget, () => selected != null);
            RemoveTargetCommand = new RelayCommand(RemoveTarget, () => selectedTarget != null);
            BackupAllCommand = new RelayCommand(() => controller.BackupAllInteractive());
            DetectAllCommand = new RelayCommand(() => controller.DetectAllInteractive());
            PruneCommand = new RelayCommand(() => controller.PruneInteractive());
            RefreshCommand = new RelayCommand(Reconcile);
            OpenVaultRootCommand = new RelayCommand(() => controller.OpenPath(controller.Store.Root));

            controller.Changed += OnControllerChanged;
            Reload();
        }

        // ------------------------------------------------------------------- collections

        public ObservableCollection<VaultGameViewModel> Games { get; private set; }

        public ObservableCollection<SnapshotViewModel> Snapshots { get; private set; }

        public ObservableCollection<TargetViewModel> Targets { get; private set; }

        // ------------------------------------------------------------------- commands

        public RelayCommand BackupCommand { get; private set; }

        public RelayCommand DetectCommand { get; private set; }

        public RelayCommand RestoreCommand { get; private set; }

        public RelayCommand DeleteCommand { get; private set; }

        public RelayCommand OpenFolderCommand { get; private set; }

        public RelayCommand<TargetViewModel> OpenPathCommand { get; private set; }

        public RelayCommand AddTargetCommand { get; private set; }

        public RelayCommand RemoveTargetCommand { get; private set; }

        public RelayCommand BackupAllCommand { get; private set; }

        public RelayCommand DetectAllCommand { get; private set; }

        public RelayCommand PruneCommand { get; private set; }

        public RelayCommand RefreshCommand { get; private set; }

        public RelayCommand OpenVaultRootCommand { get; private set; }

        // ------------------------------------------------------------------- selection

        public VaultGameViewModel SelectedGame
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }

                SetValue(ref selected, value);
                RebuildDetails();
            }
        }

        public SnapshotViewModel SelectedSnapshot
        {
            get { return selectedSnapshot; }
            set { SetValue(ref selectedSnapshot, value); }
        }

        public TargetViewModel SelectedTarget
        {
            get { return selectedTarget; }
            set { SetValue(ref selectedTarget, value); }
        }

        /// <summary>Free text filter over the game name. A 500 game library needs it.</summary>
        public string Filter
        {
            get { return filter; }
            set
            {
                if (filter == value)
                {
                    return;
                }

                SetValue(ref filter, value);
                Reload();
            }
        }

        public bool HasSelection
        {
            get { return selected != null; }
        }

        public string SelectedNameText
        {
            get { return selected == null ? Localization.Get("LOCSaveVaultPickGame", "Pick a game on the left.") : selected.NameText; }
        }

        // ------------------------------------------------------------------- footer

        /// <summary>"12 games · 48 snapshots · 310 MB of 2048 MB".</summary>
        public string TotalText
        {
            get
            {
                var profiles = controller.Store.Profiles().ToList();
                var snapshots = profiles.Sum(profile => profile.Snapshots.Count);
                var bytes = controller.Store.TotalBytes();
                var quota = (long)Math.Max(1, controller.Settings.MaxTotalMegabytes) * 1024L * 1024L;

                return VaultText.Fill("LOCSaveVaultTotals", "{0} games · {1} snapshots · {2} of {3}",
                    profiles.Count, snapshots, VaultController.FormatSize(bytes), VaultController.FormatSize(quota));
            }
        }

        /// <summary>0 - 100, drives the quota bar so a full vault is visible before it starts pruning.</summary>
        public double QuotaPercent
        {
            get
            {
                var quota = (long)Math.Max(1, controller.Settings.MaxTotalMegabytes) * 1024L * 1024L;
                var used = controller.Store.TotalBytes();
                return Math.Min(100.0, Math.Max(0.0, used * 100.0 / quota));
            }
        }

        public string RootText
        {
            get { return controller.Store.Root; }
        }

        // ------------------------------------------------------------------- plumbing

        public void Reload()
        {
            var keep = selected == null ? Guid.Empty : selected.Profile.GameId;

            suspend = true;
            Games.Clear();

            var needle = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
            foreach (var profile in controller.Store.Profiles()
                .OrderByDescending(item => item.LastBackupUtc ?? DateTime.MinValue)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var game = FindGame(profile.GameId);
                var name = game != null ? game.Name : profile.Name;
                if (needle != null && (name == null || name.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) < 0))
                {
                    continue;
                }

                Games.Add(new VaultGameViewModel(profile, game));
            }

            suspend = false;

            selected = Games.FirstOrDefault(item => item.Profile.GameId == keep) ?? Games.FirstOrDefault();
            OnPropertyChanged("SelectedGame");
            RebuildDetails();

            OnPropertyChanged("TotalText");
            OnPropertyChanged("QuotaPercent");
            OnPropertyChanged("RootText");
        }

        private void RebuildDetails()
        {
            if (suspend)
            {
                return;
            }

            var keepSnapshot = selectedSnapshot == null ? null : selectedSnapshot.Snapshot.Id;

            Snapshots.Clear();
            Targets.Clear();

            if (selected != null)
            {
                var profile = selected.Profile;
                foreach (var snapshot in profile.Snapshots.OrderByDescending(item => item.CreatedUtc))
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

            SelectedSnapshot = Snapshots.FirstOrDefault(item => item.Snapshot.Id == keepSnapshot) ?? Snapshots.FirstOrDefault();
            SelectedTarget = Targets.FirstOrDefault();

            OnPropertyChanged("HasSelection");
            OnPropertyChanged("SelectedNameText");
        }

        private Game FindGame(Guid id)
        {
            try
            {
                return api == null || api.Database == null ? null : api.Database.Games.Get(id);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnControllerChanged(object sender, EventArgs e)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.CheckAccess())
            {
                Reload();
                return;
            }

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

        /// <summary>Selects the row for a game, used when the manager is opened from that game.</summary>
        public void Focus(Game game)
        {
            if (game == null)
            {
                return;
            }

            var row = Games.FirstOrDefault(item => item.Profile.GameId == game.Id);
            if (row != null)
            {
                SelectedGame = row;
            }
        }

        // ------------------------------------------------------------------- actions

        private void Backup()
        {
            controller.BackupInteractive(selected.Game);
        }

        private void Detect()
        {
            controller.DetectInteractive(selected.Game);
        }

        private void Restore()
        {
            controller.RestoreInteractive(selected.Game, selectedSnapshot.Profile, selectedSnapshot.Snapshot);
        }

        private void Delete()
        {
            controller.DeleteInteractive(selectedSnapshot.Profile, selectedSnapshot.Snapshot);
        }

        private void OpenFolder()
        {
            controller.OpenVaultFolder(selected.Profile);
        }

        private void OpenPath(TargetViewModel item)
        {
            if (!item.IsRegistry)
            {
                controller.OpenPath(item.PathText);
            }
        }

        /// <summary>
        /// Adds a folder by hand. Manual targets carry the highest trust, so this is also the fix for
        /// a game whose saves live somewhere no heuristic will ever guess.
        /// </summary>
        private void AddTarget()
        {
            if (api == null || api.Dialogs == null)
            {
                return;
            }

            var folder = api.Dialogs.SelectFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var target = new SaveTarget
            {
                Kind = TargetKind.Folder,
                Path = SaveTarget.Normalize(folder),
                Origin = TargetOrigin.Manual,
                Confidence = 100,
                Enabled = true
            };

            var added = SaveScanner.Merge(selected.Profile, new List<SaveTarget> { target });
            controller.Store.Save();
            Reload();

            if (added == 0)
            {
                api.Dialogs.ShowMessage(
                    Localization.Get("LOCSaveVaultTargetExists", "That location is already known."),
                    VaultController.Name());
            }
        }

        private void RemoveTarget()
        {
            var question = Localization.Get("LOCSaveVaultRemoveTargetConfirm",
                "Remove this location? Existing snapshots are kept.") + "\n\n" + selectedTarget.PathText;
            if (api != null && api.Dialogs != null &&
                api.Dialogs.ShowMessage(question, VaultController.Name(), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                return;
            }

            selected.Profile.Targets.Remove(selectedTarget.Target);
            controller.Store.Save();
            Reload();
        }

        /// <summary>Re-reads the vault folder, adopting snapshots and dropping ones that are gone.</summary>
        private void Reconcile()
        {
            var changed = controller.Store.Reconcile();
            if (changed > 0)
            {
                controller.Store.Save();
            }

            Reload();
        }
    }
}
