using System;
using System.Globalization;
using SaveVault.Models;

namespace SaveVault.ViewModels
{
    /// <summary>
    /// Turns the enums and timestamps of the data model into the strings the views show.
    ///
    /// The views never format anything themselves: a snapshot list, the details panel and the
    /// notification text all describe the same record, and a single place to translate it is what
    /// keeps them from drifting apart.
    /// </summary>
    public static class VaultText
    {
        public static string Trigger(SnapshotTrigger trigger)
        {
            switch (trigger)
            {
                case SnapshotTrigger.GameStopped:
                    return Localization.Get("LOCSaveVaultTriggerGameStopped", "After play");
                case SnapshotTrigger.BeforeLaunch:
                    return Localization.Get("LOCSaveVaultTriggerBeforeLaunch", "Before launch");
                case SnapshotTrigger.Scheduled:
                    return Localization.Get("LOCSaveVaultTriggerScheduled", "Scheduled");
                case SnapshotTrigger.BeforeRestore:
                    return Localization.Get("LOCSaveVaultTriggerBeforeRestore", "Undo point");
                default:
                    return Localization.Get("LOCSaveVaultTriggerManual", "Manual");
            }
        }

        public static string Origin(TargetOrigin origin)
        {
            switch (origin)
            {
                case TargetOrigin.Learned:
                    return Localization.Get("LOCSaveVaultOriginLearned", "Observed while playing");
                case TargetOrigin.Ludusavi:
                    return Localization.Get("LOCSaveVaultOriginLudusavi", "Ludusavi manifest");
                case TargetOrigin.InstallFolder:
                    return Localization.Get("LOCSaveVaultOriginInstallFolder", "Install folder");
                case TargetOrigin.UserFolder:
                    return Localization.Get("LOCSaveVaultOriginUserFolder", "User folder");
                case TargetOrigin.RegistryGuess:
                    return Localization.Get("LOCSaveVaultOriginRegistryGuess", "Registry guess");
                default:
                    return Localization.Get("LOCSaveVaultOriginManual", "Added by hand");
            }
        }

        public static string Kind(TargetKind kind)
        {
            return kind == TargetKind.Registry
                ? Localization.Get("LOCSaveVaultKindRegistry", "Registry")
                : Localization.Get("LOCSaveVaultKindFolder", "Files");
        }

        /// <summary>Local wall clock time of a stored UTC stamp.</summary>
        public static string Moment(DateTime utc)
        {
            try
            {
                var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
                return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            }
            catch (Exception)
            {
                return utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Coarse "how long ago" text. Deliberately coarse: a save from this morning and one from
        /// three weeks ago are what the user is deciding between, and minutes of precision on the
        /// older one would only add noise.
        /// </summary>
        public static string Ago(DateTime? utc)
        {
            if (!utc.HasValue)
            {
                return Localization.Get("LOCSaveVaultAgoNever", "never");
            }

            var span = DateTime.UtcNow - utc.Value;
            if (span.TotalSeconds < 90)
            {
                return Localization.Get("LOCSaveVaultAgoNow", "just now");
            }

            if (span.TotalMinutes < 60)
            {
                return Fill("LOCSaveVaultAgoMinutes", "{0} min ago", (int)span.TotalMinutes);
            }

            if (span.TotalHours < 24)
            {
                return Fill("LOCSaveVaultAgoHours", "{0} h ago", (int)span.TotalHours);
            }

            if (span.TotalDays < 30)
            {
                return Fill("LOCSaveVaultAgoDays", "{0} d ago", (int)span.TotalDays);
            }

            return Moment(utc.Value);
        }

        /// <summary>string.Format that survives a translation with a broken placeholder.</summary>
        public static string Fill(string key, string fallback, params object[] args)
        {
            var template = Localization.Get(key, fallback);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }
}
