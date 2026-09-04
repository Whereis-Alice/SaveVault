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
        private readonly Action<VaultGameViewModel, bool> apply;

        /// <summary>
        /// Held while a deferred include/exclude call is in flight, so the tick box stays where the
        /// user put it instead of snapping back while a confirmation dialog is still open.
        /// </summary>
        private bool? pending;

        public VaultGameViewModel(GameSaveProfile profile, Game game, bool detached = false,
            Action<VaultGameViewModel, bool> apply = null)
        {
            Profile = profile;
            Game = game;
            IsDetached = detached;
            this.apply = apply;
        }

        public GameSaveProfile Profile { get; private set; }

        /// <summary>
        /// True for a row that stands for a library game the vault has no record of yet. Listing
        /// those is what makes "back up everything except..." possible - you cannot untick a game
        /// that is not on screen.
        /// </summary>
        public bool IsDetached { get; private set; }

        /// <summary>Promotes the row to a real record, once there is something to write down.</summary>
        public void Attach(GameSaveProfile stored)
        {
            Profile = stored;
            IsDetached = false;
            Refresh();
        }

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
            get { return Bytes == 0 ? string.Empty : VaultController.FormatSize(Bytes); }
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

        /// <summary>
        /// The row's tick box. Being backed up is the default, so this is stored inverted: only an
        /// exclusion is ever written to the index.
        /// </summary>
        public bool IsIncluded
        {
            get { return pending ?? !Profile.Excluded; }
            set
            {
                if (IsIncluded == value)
                {
                    return;
                }

                pending = value;
                OnPropertyChanged("IsIncluded");
                OnPropertyChanged("IsExcluded");
                OnPropertyChanged("RowOpacity");

                if (apply != null)
                {
                    apply(this, value);
                }
            }
        }

        public bool IsExcluded
        {
            get { return !IsIncluded; }
        }

        /// <summary>Dims a skipped row so it reads as skipped without having to look for a label.</summary>
        public double RowOpacity
        {
            get { return IsIncluded ? 1.0 : 0.55; }
        }

        public void Refresh()
        {
            pending = null;
            OnPropertyChanged("NameText");
            OnPropertyChanged("SizeText");
            OnPropertyChanged("StatusText");
            OnPropertyChanged("IsIncluded");
            OnPropertyChanged("IsExcluded");
            OnPropertyChanged("RowOpacity");
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
        private int scope;

        private const int ScopeVault = 0;
        private const int ScopeAll = 1;
        private const int ScopeExcluded = 2;

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
            SelectAllCommand = new RelayCommand(() => SetAll(true));
            SelectNoneCommand = new RelayCommand(() => SetAll(false));
            PurgeExcludedCommand = new RelayCommand(() => controller.PurgeExcludedInteractive());
            OpenSettingsCommand = new RelayCommand(() =>
            {
                var open = OpenSettingsAction;
                if (open != null)
                {
                    open();
                }
            }, () => OpenSettingsAction != null);

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

        public RelayCommand SelectAllCommand { get; private set; }

        public RelayCommand SelectNoneCommand { get; private set; }

        public RelayCommand PurgeExcludedCommand { get; private set; }

        public RelayCommand OpenSettingsCommand { get; private set; }

        /// <summary>
        /// Supplied by the plugin, which owns the window and can only open its settings once this
        /// dialog has closed. Null when there is nowhere to go.
        /// </summary>
        public Action OpenSettingsAction { get; set; }

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

        /// <summary>
        /// What the left hand list shows: the vault's own records, the whole library, or only the
        /// games that are currently skipped.
        /// </summary>
        public int ScopeIndex
        {
            get { return scope; }
            set
            {
                if (scope == value)
                {
                    return;
                }

                SetValue(ref scope, value);
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

        /// <summary>
        /// "3 excluded · 1.2 GB still stored". The second half is the point: unticking a game stops
        /// future backups but frees nothing on its own.
        /// </summary>
        public string ExcludedText
        {
            get
            {
                var excluded = controller.Store.Profiles().Where(profile => profile.Excluded).ToList();
                if (excluded.Count == 0)
                {
                    return Localization.Get("LOCSaveVaultExcludeNone", "No game is excluded.");
                }

                var bytes = excluded.Sum(profile => profile.Snapshots.Sum(snapshot => snapshot.Bytes));
                return VaultText.Fill("LOCSaveVaultExcludeSummary", "{0} excluded · {1} still stored",
                    excluded.Count, VaultController.FormatSize(bytes));
            }
        }

        /// <summary>Drives the cleanup link, which is pointless when there is nothing to delete.</summary>
        public bool HasExcludedSnapshots
        {
            get { return controller.Store.Profiles().Any(profile => profile.Excluded && profile.Snapshots.Count > 0); }
        }

        // ------------------------------------------------------------------- plumbing

        public void Reload()
        {
            var keep = selected == null ? Guid.Empty : selected.Profile.GameId;

            suspend = true;
            Games.Clear();

            var needle = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
            var known = new HashSet<Guid>();

            foreach (var profile in controller.Store.Profiles()
                .OrderByDescending(item => item.LastBackupUtc ?? DateTime.MinValue)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                known.Add(profile.GameId);
                if (scope == ScopeExcluded && !profile.Excluded)
                {
                    continue;
                }

                var game = FindGame(profile.GameId);
                if (!Matches(game != null ? game.Name : profile.Name, needle))
                {
                    continue;
                }

                Games.Add(NewRow(profile, game, false));
            }

            if (scope == ScopeAll && api != null && api.Database != null)
            {
                // Games the vault has never touched. They come last: every row above has something
                // to report, these exist only so they can be ticked off.
                foreach (var game in api.Database.Games
                    .Where(item => item != null && !item.Hidden && !known.Contains(item.Id) && Matches(item.Name, needle))
                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Games.Add(NewRow(Detached(game), game, true));
                }
            }

            suspend = false;

            selected = Games.FirstOrDefault(item => item.Profile.GameId == keep) ?? Games.FirstOrDefault();
            OnPropertyChanged("SelectedGame");
            RebuildDetails();

            OnPropertyChanged("TotalText");
            OnPropertyChanged("QuotaPercent");
            OnPropertyChanged("RootText");
            OnPropertyChanged("ExcludedText");
            OnPropertyChanged("HasExcludedSnapshots");
        }

        private VaultGameViewModel NewRow(GameSaveProfile profile, Game game, bool detached)
        {
            return new VaultGameViewModel(profile, game, detached, SetIncluded);
        }

        /// <summary>A stand-in record so a library game with no history can still be listed.</summary>
        private static GameSaveProfile Detached(Game game)
        {
            return new GameSaveProfile
            {
                GameId = game.Id,
                Name = game.Name,
                Folder = PathTokens.SanitizeFolderName(game.Name, game.Id)
            };
        }

        private static bool Matches(string name, string needle)
        {
            if (needle == null)
            {
                return true;
            }

            return name != null && name.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>
        /// Runs work once the current input event is over. Excluding a game can raise a
        /// confirmation dialog, and opening one from inside a tick box's binding write leaves WPF
        /// holding the value it started with.
        /// </summary>
        private static void Defer(Action work)
        {
            var app = Application.Current;
            if (app == null)
            {
                work();
                return;
            }

            app.Dispatcher.BeginInvoke(work);
        }

        /// <summary>Applies a single row's tick box.</summary>
        private void SetIncluded(VaultGameViewModel row, bool included)
        {
            var game = row.Game;
            var profile = row.Profile;

            Defer(() =>
            {
                if (game != null)
                {
                    controller.SetExcluded(new List<Game> { game }, !included, true);
                }
                else
                {
                    controller.SetExcluded(new List<GameSaveProfile> { profile }, !included, true);
                }

                Reload();
            });
        }

        /// <summary>
        /// Ticks or unticks everything currently listed. It deliberately obeys the filter and the
        /// scope, which makes "type BALDR, untick all" the quickest way to skip a whole series.
        /// </summary>
        private void SetAll(bool included)
        {
            var rows = Games.Where(row => row.IsIncluded != included).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            if (!included && rows.Count > 10 && api != null && api.Dialogs != null &&
                api.Dialogs.ShowMessage(
                    VaultText.Fill("LOCSaveVaultExcludeManyAsk", "Stop backing up all {0} listed games?", rows.Count),
                    VaultController.Name(), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                Reload();
                return;
            }

            var profiles = new List<GameSaveProfile>();
            foreach (var row in rows)
            {
                if (row.Game == null)
                {
                    profiles.Add(row.Profile);
                    continue;
                }

                // Included is the default, so only an exclusion needs a record of its own.
                var stored = included ? controller.Store.Find(row.Game.Id) : controller.Store.GetOrCreate(row.Game);
                if (stored != null)
                {
                    profiles.Add(stored);
                }
            }

            Defer(() =>
            {
                controller.SetExcluded(profiles, !included, true);
                Reload();
            });
        }

        /// <summary>
        /// Turns a listed but unknown game into a real record, right before something has to be
        /// written to it.
        /// </summary>
        private GameSaveProfile Materialize(VaultGameViewModel row)
        {
            if (row.IsDetached && row.Game != null)
            {
                row.Attach(controller.Store.GetOrCreate(row.Game));
                controller.Store.Save();
            }

            return row.Profile;
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
            Materialize(selected);
            controller.BackupInteractive(selected.Game);
        }

        private void Detect()
        {
            Materialize(selected);
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
            controller.OpenVaultFolder(Materialize(selected));
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

            var added = SaveScanner.Merge(Materialize(selected), new List<SaveTarget> { target });
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
