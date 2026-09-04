using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SaveVault.Models;
using SaveVault.Services;
using SaveVault.ViewModels;
using SaveVault.Views;

namespace SaveVault
{
    /// <summary>
    /// Versioned save backups for the whole library.
    ///
    /// The plugin owns one <see cref="VaultController" /> and one panel view model, so a backup
    /// started from the game menu, from the details panel or from the scheduler all go through the
    /// same lock and all refresh the same UI.
    /// </summary>
    public class SaveVaultPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly SaveVaultSettings settings;
        private readonly VaultStore store;
        private readonly VaultController controller;
        private readonly SettingsViewModel settingsModel;
        private readonly LearningWatcher learning;
        private readonly Scheduler scheduler;

        private GameVaultViewModel panel;
        private VaultManagerViewModel manager;

        public override Guid Id { get; } = Guid.Parse("fd085db8-9b7c-4f83-a2df-3f0784eae1d0");

        public SaveVaultPlugin(IPlayniteAPI api) : base(api)
        {
            settings = LoadPluginSettings<SaveVaultSettings>() ?? new SaveVaultSettings();

            // The root is read through a delegate rather than captured: the user can move the vault
            // in settings and every later call has to see the new folder without anything being
            // rebuilt.
            store = new VaultStore(() => settings.EffectiveBackupRoot);
            controller = new VaultController(api, settings, store);
            settingsModel = new SettingsViewModel(settings, controller, api, () => SavePluginSettings(settings));
            learning = new LearningWatcher(settings);
            scheduler = new Scheduler(RunScheduled);

            Properties = new GenericPluginProperties { HasSettings = true };

            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "SaveVault",
                ElementList = new List<string> { "GameVaultControl" }
            });

            // Strings and theme brushes are needed the first time a control renders, which can
            // happen before OnApplicationStarted fires, so both are primed here as well.
            Localization.Load(PluginFolder, api == null ? null : api.ApplicationSettings.Language);
            ThemeBridge.EnsureDefaults();
        }

        /// <summary>Folder the add-on was installed into, which is where Localization lives.</summary>
        private static string PluginFolder
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        /// <summary>
        /// Created on demand so a library whose theme never mounts the panel pays nothing, and so
        /// the first control to appear cannot race a second one into a second view model.
        /// </summary>
        private GameVaultViewModel Panel
        {
            get
            {
                if (panel == null)
                {
                    panel = new GameVaultViewModel(controller, OpenManager);
                }

                return panel;
            }
        }

        /// <summary>
        /// Kept alive between openings. Rebuilding it each time would re-subscribe to
        /// VaultController.Changed and leak one handler per window.
        /// </summary>
        private VaultManagerViewModel Manager
        {
            get
            {
                if (manager == null)
                {
                    manager = new VaultManagerViewModel(controller, PlayniteApi);
                }

                return manager;
            }
        }

        // ------------------------------------------------------------------ lifecycle

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // The active theme is merged by now, so this pass lets a theme's own values win while
            // still filling in anything it left undefined.
            ThemeBridge.EnsureDefaults();

            // Adopts snapshots written by another machine or an older version, and drops index
            // entries whose zip is gone, before anything reports a size.
            try
            {
                if (store.Reconcile() > 0)
                {
                    store.Save();
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: could not reconcile the vault folder.");
            }

            // One time cleanup after the plausibility rules were tightened. Version 1.0.0 learned
            // any folder that changed while a game was running, so libraries carry records that
            // point at messengers and driver panels; they have to go before the next backup copies
            // them again. Off the startup thread because it walks folders to measure them.
            if (settings.LearnedGuardVersion < LearnedGuard.Version)
            {
                Task.Run(() =>
                {
                    try
                    {
                        var dropped = controller.PurgeImplausibleLearned();
                        settings.LearnedGuardVersion = LearnedGuard.Version;
                        SavePluginSettings(settings);

                        if (dropped > 0)
                        {
                            controller.Notify("savevault-purge",
                                Localization.Fill("LOCSaveVaultPurgeNotice",
                                    "Save Vault removed {0} save locations that were recorded by mistake.", dropped),
                                NotificationType.Info);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Save Vault: could not re-check learned locations.");
                    }
                });
            }

            scheduler.Start();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            scheduler.Stop();
            learning.Clear();
            SavePluginSettings(settings);
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            // Only relevant once a control exists; PluginUserControl.GameContextChanged drives the
            // mounted instance and this keeps the shared model right for the menu actions.
            if (panel == null)
            {
                return;
            }

            var selection = args == null ? null : args.NewValue;
            panel.SetGame(selection != null && selection.Count == 1 ? selection[0] : null);
        }

        /// <summary>
        /// Runs before the game process is created, which is the only moment a pre-launch snapshot
        /// is worth anything, and the last moment detection can still be primed for free.
        /// </summary>
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var game = args == null ? null : args.Game;
            if (game == null)
            {
                return;
            }

            try
            {
                var profile = store.Find(game.Id);
                if (settings.AutoScanNewGames && (profile == null || !profile.HasTargets))
                {
                    controller.Detect(game);
                }

                if (settings.BackupBeforeLaunch)
                {
                    controller.Backup(game, SnapshotTrigger.BeforeLaunch, false);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: pre-launch step failed for " + game.Name);
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            var game = args == null ? null : args.Game;
            if (game == null || !settings.RuntimeLearning)
            {
                return;
            }

            // Baseline now, diff on exit: the folders a game writes while running are the only
            // reliable signal for the many titles no manifest and no heuristic will ever cover.
            learning.Start(game);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var game = args == null ? null : args.Game;
            if (game == null)
            {
                return;
            }

            // Off the event thread: hashing and zipping a save folder must not hold up Playnite's
            // own post-launch bookkeeping.
            Task.Run(() => AfterPlay(game));
        }

        /// <summary>Learning diff first, then the snapshot, so a newly learned folder is captured
        /// by the very same backup instead of only the next one.</summary>
        private void AfterPlay(Game game)
        {
            try
            {
                var learned = learning.Stop(game.Id);
                if (learned != null && learned.Count > 0)
                {
                    var profile = store.GetOrCreate(game);
                    if (SaveScanner.Merge(profile, learned) > 0)
                    {
                        store.Save();
                        logger.Info("Save Vault: learned " + learned.Count + " location(s) from " + game.Name);
                    }
                }

                if (settings.BackupOnGameStopped)
                {
                    controller.Backup(game, SnapshotTrigger.GameStopped, false);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: post-play step failed for " + game.Name);
            }
        }

        /// <summary>
        /// The scheduler ticks every few minutes; the interval itself is enforced here so that
        /// changing it in settings takes effect immediately and a tick missed during sleep is
        /// caught by the next one rather than lost.
        /// </summary>
        private void RunScheduled()
        {
            if (!settings.ScheduledBackupEnabled)
            {
                return;
            }

            var last = settings.LastScheduledRunUtc;
            var due = Math.Max(15, settings.ScheduleIntervalMinutes);
            if (last.HasValue && (DateTime.UtcNow - last.Value).TotalMinutes < due)
            {
                return;
            }

            controller.BackupScheduled();
            SavePluginSettings(settings);
        }

        // ------------------------------------------------------------------ views

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args == null || args.Name != "GameVaultControl")
            {
                return null;
            }

            return new GameVaultControl { DataContext = Panel };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SettingsView { DataContext = settingsModel };
        }

        /// <summary>
        /// Opens the manager, optionally scrolled to one game. Everything that needs room - older
        /// snapshots, pinning, fixing a wrong path - lives here rather than in the panel.
        /// </summary>
        private void OpenManager(Game game)
        {
            try
            {
                var model = Manager;
                model.Reload();
                model.Focus(game);

                var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true
                });

                window.Title = Localization.Get("LOCSaveVaultName", "Save Vault");
                window.Content = new VaultManagerView { DataContext = model };
                window.Width = 1020;
                window.Height = 640;
                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
                window.ShowDialog();
            }
            catch (Exception e)
            {
                logger.Error(e, "Save Vault: could not open the manager window.");
            }
        }

        // ------------------------------------------------------------------ menus

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var section = VaultController.Name();
            var games = args == null || args.Games == null ? new List<Game>() : args.Games;
            var single = games.Count == 1 ? games[0] : null;

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuBackup", "Back up now"),
                Action = context => BackupMany(context.Games)
            };

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuDetect", "Find save locations"),
                Action = context => DetectMany(context.Games)
            };

            if (single != null)
            {
                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = Localization.Get("LOCSaveVaultMenuManage", "Open Save Vault..."),
                    Action = context => OpenManager(single)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = Localization.Get("LOCSaveVaultMenuAddTarget", "Add a save location..."),
                    Action = context => AddTarget(single)
                };

                yield return new GameMenuItem
                {
                    MenuSection = section,
                    Description = Localization.Get("LOCSaveVaultMenuOpenFolder", "Open the backup folder"),
                    Action = context => OpenFolder(single)
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuExclude", "Skip during library backups"),
                Action = context => ToggleExcluded(context.Games)
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var section = "@" + VaultController.Name();

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuManage", "Open Save Vault..."),
                Action = context => OpenManager(null)
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuBackupAll", "Back up the whole library"),
                Action = context => controller.BackupAllInteractive()
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuDetectAll", "Find save locations for every game"),
                Action = context => controller.DetectAllInteractive()
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuPrune", "Apply the retention policy"),
                Action = context => controller.PruneInteractive()
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = Localization.Get("LOCSaveVaultMenuPurgeLearned", "Re-check learned locations"),
                Action = context => controller.PurgeLearnedInteractive()
            };
        }

        // ------------------------------------------------------------------ actions

        /// <summary>
        /// One game goes through the interactive path so the user gets the usual summary; a
        /// selection is batched behind a single cancellable progress dialog instead of one dialog
        /// and one message box per game.
        /// </summary>
        private void BackupMany(List<Game> games)
        {
            if (games == null || games.Count == 0)
            {
                return;
            }

            if (games.Count == 1)
            {
                controller.BackupInteractive(games[0]);
                return;
            }

            var created = 0;
            var unchanged = 0;
            var empty = 0;
            var failed = 0;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
                {
                    progress.ProgressMaxValue = games.Count;
                    foreach (var game in games)
                    {
                        if (progress.CancelToken.IsCancellationRequested)
                        {
                            break;
                        }

                        progress.Text = VaultController.Name() + "\n" + game.Name;
                        var result = controller.Backup(game, SnapshotTrigger.Manual, false);
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

                        progress.CurrentProgressValue++;
                    }
                },
                new GlobalProgressOptions(VaultController.Name(), true) { IsIndeterminate = false });

            PlayniteApi.Dialogs.ShowMessage(
                Localization.Get("LOCSaveVaultBackupAllDone", "Library backup finished.") + "\n" +
                Localization.Get("LOCSaveVaultStatCreated", "New snapshots:") + " " + created + "\n" +
                Localization.Get("LOCSaveVaultStatUnchanged", "Unchanged:") + " " + unchanged + "\n" +
                Localization.Get("LOCSaveVaultStatNoTargets", "No known location:") + " " + empty +
                (failed > 0 ? "\n" + Localization.Get("LOCSaveVaultStatFailed", "Failed:") + " " + failed : string.Empty),
                VaultController.Name());
        }

        private void DetectMany(List<Game> games)
        {
            if (games == null || games.Count == 0)
            {
                return;
            }

            if (games.Count == 1)
            {
                controller.DetectInteractive(games[0]);
                return;
            }

            var added = 0;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
                {
                    progress.ProgressMaxValue = games.Count;
                    foreach (var game in games)
                    {
                        if (progress.CancelToken.IsCancellationRequested)
                        {
                            break;
                        }

                        progress.Text = VaultController.Name() + "\n" + game.Name;
                        added += controller.Detect(game, false);
                        progress.CurrentProgressValue++;
                    }

                    store.Save();
                },
                new GlobalProgressOptions(VaultController.Name(), true) { IsIndeterminate = false });

            PlayniteApi.Dialogs.ShowMessage(
                Localization.Get("LOCSaveVaultDetectDone", "Detection finished.") + "\n" +
                Localization.Get("LOCSaveVaultDetectTotal", "Known locations:") + " +" + added,
                VaultController.Name());
        }

        /// <summary>
        /// Adds a folder by hand. Manual targets carry the highest trust, which makes this the fix
        /// for a game whose saves live somewhere no heuristic will ever guess.
        /// </summary>
        private void AddTarget(Game game)
        {
            var folder = PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var profile = store.GetOrCreate(game);
            var target = new SaveTarget
            {
                Kind = TargetKind.Folder,
                Path = SaveTarget.Normalize(folder),
                Origin = TargetOrigin.Manual,
                Confidence = 100,
                Enabled = true
            };

            var added = SaveScanner.Merge(profile, new List<SaveTarget> { target });
            store.Save();

            if (panel != null)
            {
                panel.Reload();
            }

            if (manager != null)
            {
                manager.Reload();
            }

            if (added == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    Localization.Get("LOCSaveVaultTargetExists", "That location is already known."),
                    VaultController.Name());
            }
        }

        private void OpenFolder(Game game)
        {
            var profile = store.Find(game.Id);
            if (profile == null)
            {
                controller.OpenPath(store.Root);
                return;
            }

            controller.OpenVaultFolder(profile);
        }

        /// <summary>
        /// Flips the flag for the whole selection. Excluded games keep their snapshots and can
        /// still be backed up by hand; they are only skipped by the library wide passes.
        /// </summary>
        private void ToggleExcluded(List<Game> games)
        {
            if (games == null || games.Count == 0)
            {
                return;
            }

            var target = !games.All(game =>
            {
                var profile = store.Find(game.Id);
                return profile != null && profile.Excluded;
            });

            foreach (var game in games)
            {
                store.GetOrCreate(game).Excluded = target;
            }

            store.Save();

            if (panel != null)
            {
                panel.Reload();
            }

            if (manager != null)
            {
                manager.Reload();
            }
        }
    }
}
