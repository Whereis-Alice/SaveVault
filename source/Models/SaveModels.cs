using System;
using System.Collections.Generic;
using Playnite.SDK.Data;

namespace SaveVault.Models
{
    /// <summary>What a save target physically is. Registry targets are exported as .reg text.</summary>
    public enum TargetKind
    {
        Folder = 0,
        Registry = 1
    }

    /// <summary>
    /// Where a target came from. The order doubles as the trust order used when the
    /// scanner merges results: a manually entered path always wins over a guess, and a
    /// path that was observed being written to during play beats a static heuristic.
    /// </summary>
    public enum TargetOrigin
    {
        Manual = 0,
        Learned = 1,
        Ludusavi = 2,
        InstallFolder = 3,
        UserFolder = 4,
        RegistryGuess = 5
    }

    /// <summary>Why a snapshot was taken. Stored on the snapshot so pruning can be selective.</summary>
    public enum SnapshotTrigger
    {
        Manual = 0,
        GameStopped = 1,
        BeforeLaunch = 2,
        Scheduled = 3,
        BeforeRestore = 4
    }

    /// <summary>One place a game keeps its saves.</summary>
    public class SaveTarget
    {
        [SerializationPropertyName("kind")]
        public TargetKind Kind { get; set; }

        /// <summary>Absolute folder path, or a registry path such as HKEY_CURRENT_USER\Software\Vendor\Game.</summary>
        [SerializationPropertyName("path")]
        public string Path { get; set; }

        [SerializationPropertyName("origin")]
        public TargetOrigin Origin { get; set; }

        /// <summary>0-100. Only used to sort and to decide what gets enabled automatically.</summary>
        [SerializationPropertyName("confidence")]
        public int Confidence { get; set; }

        /// <summary>False keeps a detected path on record without backing it up.</summary>
        [SerializationPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Optional semicolon separated file patterns. Empty means the whole folder.
        /// Used when saves sit loose next to the executable, where copying the folder
        /// would mean copying the whole game.
        /// </summary>
        [SerializationPropertyName("filter")]
        public string Filter { get; set; }

        [SerializationPropertyName("note")]
        public string Note { get; set; }

        [DontSerialize]
        public bool IsRegistry
        {
            get { return Kind == TargetKind.Registry; }
        }

        /// <summary>Targets are identified by kind + path, case-insensitively.</summary>
        public bool SameAs(SaveTarget other)
        {
            if (other == null || other.Kind != Kind)
            {
                return false;
            }

            return string.Equals(Normalize(Path), Normalize(other.Path), StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return path.Trim().TrimEnd('\\', '/');
        }

        public SaveTarget Clone()
        {
            return new SaveTarget
            {
                Kind = Kind,
                Path = Path,
                Origin = Origin,
                Confidence = Confidence,
                Enabled = Enabled,
                Filter = Filter,
                Note = Note
            };
        }
    }

    /// <summary>One archived copy of a game's saves.</summary>
    public class SnapshotRecord
    {
        /// <summary>Timestamp based id, also the archive file name without extension.</summary>
        [SerializationPropertyName("id")]
        public string Id { get; set; }

        [SerializationPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; }

        [SerializationPropertyName("trigger")]
        public SnapshotTrigger Trigger { get; set; }

        [SerializationPropertyName("bytes")]
        public long Bytes { get; set; }

        [SerializationPropertyName("files")]
        public int Files { get; set; }

        /// <summary>Content fingerprint of the sources, used to skip unchanged backups.</summary>
        [SerializationPropertyName("hash")]
        public string Hash { get; set; }

        /// <summary>Original locations, so a restore still works after the index is lost.</summary>
        [SerializationPropertyName("sources")]
        public List<SaveTarget> Sources { get; set; } = new List<SaveTarget>();

        /// <summary>Set once a snapshot is pinned; pinned snapshots survive every retention rule.</summary>
        [SerializationPropertyName("pinned")]
        public bool Pinned { get; set; }

        [SerializationPropertyName("note")]
        public string Note { get; set; }
    }

    /// <summary>Everything the vault knows about one game.</summary>
    public class GameSaveProfile
    {
        [SerializationPropertyName("gameId")]
        public Guid GameId { get; set; }

        [SerializationPropertyName("name")]
        public string Name { get; set; }

        /// <summary>Folder name used inside the vault root. Kept stable once assigned.</summary>
        [SerializationPropertyName("folder")]
        public string Folder { get; set; }

        [SerializationPropertyName("targets")]
        public List<SaveTarget> Targets { get; set; } = new List<SaveTarget>();

        [SerializationPropertyName("snapshots")]
        public List<SnapshotRecord> Snapshots { get; set; } = new List<SnapshotRecord>();

        [SerializationPropertyName("scannedUtc")]
        public DateTime? ScannedUtc { get; set; }

        [SerializationPropertyName("lastBackupUtc")]
        public DateTime? LastBackupUtc { get; set; }

        /// <summary>Hash of the last snapshot, so an unchanged game does not pile up copies.</summary>
        [SerializationPropertyName("lastHash")]
        public string LastHash { get; set; }

        /// <summary>Excluded games are skipped by every automatic trigger.</summary>
        [SerializationPropertyName("excluded")]
        public bool Excluded { get; set; }

        [DontSerialize]
        public bool HasTargets
        {
            get { return Targets != null && Targets.Count > 0; }
        }

        public GameSaveProfile Clone()
        {
            var clone = new GameSaveProfile
            {
                GameId = GameId,
                Name = Name,
                Folder = Folder,
                ScannedUtc = ScannedUtc,
                LastBackupUtc = LastBackupUtc,
                LastHash = LastHash,
                Excluded = Excluded
            };

            foreach (var target in Targets)
            {
                clone.Targets.Add(target.Clone());
            }

            clone.Snapshots.AddRange(Snapshots);
            return clone;
        }
    }

    /// <summary>Root of the on-disk index that lives inside the vault folder.</summary>
    public class VaultIndex
    {
        public const int CurrentSchema = 1;

        [SerializationPropertyName("schema")]
        public int Schema { get; set; } = CurrentSchema;

        [SerializationPropertyName("games")]
        public List<GameSaveProfile> Games { get; set; } = new List<GameSaveProfile>();
    }

    /// <summary>Result of one backup attempt, reported back to the caller for messaging.</summary>
    public class BackupResult
    {
        public bool Created { get; set; }
        public bool Unchanged { get; set; }
        public bool NoTargets { get; set; }
        public bool Skipped { get; set; }

        /// <summary>Set when the sources are larger than the per snapshot budget, so nothing was written.</summary>
        public bool TooLarge { get; set; }

        /// <summary>Measured size of the sources, filled in whether or not a snapshot was written.</summary>
        public long SourceBytes { get; set; }

        /// <summary>Measured number of source files.</summary>
        public int SourceFiles { get; set; }
        public string Error { get; set; }
        public SnapshotRecord Snapshot { get; set; }
        public string GameName { get; set; }

        public bool Failed
        {
            get { return !string.IsNullOrEmpty(Error); }
        }
    }
}
