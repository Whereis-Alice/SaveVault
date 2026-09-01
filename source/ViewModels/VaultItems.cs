using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SaveVault.Models;
using SaveVault.Services;

namespace SaveVault.ViewModels
{
    /// <summary>
    /// One row in a target list. Wraps a <see cref="SaveTarget" /> so the view can bind localized
    /// text and a live "does this path still exist" flag without the model having to know about
    /// either.
    /// </summary>
    public class TargetViewModel : ObservableObject
    {
        private readonly SaveTarget target;
        private readonly Action persist;
        private bool missing;

        public TargetViewModel(SaveTarget target, Action persist)
        {
            this.target = target;
            this.persist = persist;
            missing = !Probe();
        }

        public SaveTarget Target
        {
            get { return target; }
        }

        public string PathText
        {
            get { return target.Path; }
        }

        public string OriginText
        {
            get { return VaultText.Origin(target.Origin); }
        }

        public string KindText
        {
            get { return VaultText.Kind(target.Kind); }
        }

        public string FilterText
        {
            get { return string.IsNullOrWhiteSpace(target.Filter) ? null : target.Filter; }
        }

        public string NoteText
        {
            get { return target.Note; }
        }

        public bool IsRegistry
        {
            get { return target.IsRegistry; }
        }

        /// <summary>
        /// Shown as a warning rather than hidden. A path that used to hold saves and no longer does
        /// usually means the game was reinstalled elsewhere, which is exactly when the user needs to
        /// know that this entry is no longer being backed up.
        /// </summary>
        public bool Missing
        {
            get { return missing; }
            private set { SetValue(ref missing, value); }
        }

        /// <summary>Disabled targets stay in the profile so detection does not keep re-adding them.</summary>
        public bool Enabled
        {
            get { return target.Enabled; }
            set
            {
                if (target.Enabled == value)
                {
                    return;
                }

                target.Enabled = value;
                OnPropertyChanged("Enabled");
                if (persist != null)
                {
                    persist();
                }
            }
        }

        public void Refresh()
        {
            Missing = !Probe();
            OnPropertyChanged("Enabled");
        }

        private bool Probe()
        {
            try
            {
                if (target.IsRegistry)
                {
                    return RegistryBridge.Exists(target.Path);
                }

                return Directory.Exists(target.Path) || File.Exists(target.Path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>One row in a snapshot list.</summary>
    public class SnapshotViewModel : ObservableObject
    {
        private readonly GameSaveProfile profile;
        private readonly SnapshotRecord snapshot;
        private readonly Action<SnapshotViewModel> pin;

        public SnapshotViewModel(GameSaveProfile profile, SnapshotRecord snapshot, Action<SnapshotViewModel> pin)
        {
            this.profile = profile;
            this.snapshot = snapshot;
            this.pin = pin;
        }

        public GameSaveProfile Profile
        {
            get { return profile; }
        }

        public SnapshotRecord Snapshot
        {
            get { return snapshot; }
        }

        public string MomentText
        {
            get { return VaultText.Moment(snapshot.CreatedUtc); }
        }

        public string AgoText
        {
            get { return VaultText.Ago(snapshot.CreatedUtc); }
        }

        public string TriggerText
        {
            get { return VaultText.Trigger(snapshot.Trigger); }
        }

        public string SizeText
        {
            get { return VaultController.FormatSize(snapshot.Bytes); }
        }

        /// <summary>"42 files, 1.3 MB" - the two numbers that tell a good snapshot from a broken one.</summary>
        public string DetailText
        {
            get
            {
                var parts = new List<string>();
                if (snapshot.Files > 0)
                {
                    parts.Add(snapshot.Files + " " + Localization.Get("LOCSaveVaultFilesWord", "files"));
                }

                var registry = snapshot.Sources == null
                    ? 0
                    : snapshot.Sources.Count(source => source.Kind == TargetKind.Registry);
                if (registry > 0)
                {
                    parts.Add(registry + " " + Localization.Get("LOCSaveVaultRegistryWord", "registry keys"));
                }

                parts.Add(SizeText);
                return string.Join("  ·  ", parts);
            }
        }

        public string SourcesText
        {
            get
            {
                if (snapshot.Sources == null || snapshot.Sources.Count == 0)
                {
                    return null;
                }

                return string.Join("\n", snapshot.Sources.Select(source => source.Path));
            }
        }

        public bool Pinned
        {
            get { return snapshot.Pinned; }
            set
            {
                if (snapshot.Pinned == value)
                {
                    return;
                }

                if (pin != null)
                {
                    pin(this);
                }

                OnPropertyChanged("Pinned");
            }
        }

        public string NoteText
        {
            get { return snapshot.Note; }
        }
    }
}
