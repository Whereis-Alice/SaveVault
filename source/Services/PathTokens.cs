using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SaveVault.Services
{
    /// <summary>
    /// Path helpers shared by every detection layer: expansion of the placeholder tokens
    /// used by the Ludusavi manifest, the list of user folders worth scanning, and the
    /// sanitising used for vault folder names.
    /// </summary>
    public static class PathTokens
    {
        /// <summary>
        /// Folders a game is likely to write saves into. Ordered roughly by how often that
        /// happens so that the first match also tends to be the best one.
        /// </summary>
        public static IEnumerable<string> UserRoots()
        {
            var roots = new List<string>();

            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Add(roots, LocalLow());
            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"));
            Add(roots, SavedGames());
            Add(roots, VirtualStore());

            return roots;
        }

        /// <summary>
        /// Roots watched while a game runs. Same list as <see cref="UserRoots"/> minus the
        /// derived sub folders, because the watcher walks recursively anyway.
        /// </summary>
        public static IEnumerable<string> LearningRoots()
        {
            var roots = new List<string>();

            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Add(roots, LocalLow());
            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add(roots, SavedGames());

            return roots;
        }

        public static string LocalLow()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                return null;
            }

            var parent = Path.GetDirectoryName(local);
            return parent == null ? null : Path.Combine(parent, "LocalLow");
        }

        public static string SavedGames()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Saved Games");
        }

        /// <summary>
        /// UAC redirection target. Old Japanese games installed under Program Files write
        /// here without knowing it, and their saves are invisible everywhere else.
        /// </summary>
        public static string VirtualStore()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "VirtualStore");
        }

        private static void Add(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(path);
            }
        }

        /// <summary>
        /// Turns a Ludusavi manifest path into a concrete Windows path. Returns null when a
        /// token cannot be resolved on this machine, which is the signal to skip the entry.
        /// </summary>
        public static string Expand(string raw, string installDir)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var text = raw.Trim().Replace('/', '\\');

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "<base>", installDir },
                { "<root>", installDir },
                { "<game>", installDir },
                { "<home>", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
                { "<winAppData>", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) },
                { "<winLocalAppData>", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) },
                { "<winLocalAppDataLow>", LocalLow() },
                { "<winDocuments>", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                { "<winPublic>", Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) },
                { "<winProgramData>", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) },
                { "<winDir>", Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
                { "<osUserName>", Environment.UserName },
                { "<winSavedGames>", SavedGames() }
            };

            foreach (var pair in map)
            {
                var index = text.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(pair.Value))
                {
                    return null;
                }

                text = text.Substring(0, index) + pair.Value + text.Substring(index + pair.Key.Length);
            }

            // Anything still wrapped in angle brackets is a token we do not support, such as
            // the store specific user ids. Those cannot be guessed, so drop the entry.
            if (text.IndexOf('<') >= 0)
            {
                return null;
            }

            return text.TrimEnd('\\');
        }

        /// <summary>
        /// Splits an expanded manifest path into the deepest wildcard free directory and the
        /// remaining pattern. Ludusavi uses * and ** to cover profile folders; the caller
        /// resolves those by enumerating the parent instead of failing on the whole entry.
        /// </summary>
        public static void SplitWildcard(string path, out string root, out string pattern)
        {
            root = path;
            pattern = null;

            if (string.IsNullOrEmpty(path) || path.IndexOf('*') < 0)
            {
                return;
            }

            var parts = path.Split('\\');
            var head = new List<string>();
            var tail = new List<string>();

            foreach (var part in parts)
            {
                if (tail.Count == 0 && part.IndexOf('*') < 0)
                {
                    head.Add(part);
                }
                else
                {
                    tail.Add(part);
                }
            }

            root = string.Join("\\", head);
            pattern = string.Join("\\", tail);
        }

        private static readonly char[] invalid = Path.GetInvalidFileNameChars();

        /// <summary>Vault folder name for a game: readable, unique enough, and safe on NTFS.</summary>
        public static string SanitizeFolderName(string name, Guid id)
        {
            var builder = new StringBuilder();
            var text = string.IsNullOrWhiteSpace(name) ? "Game" : name.Trim();

            foreach (var c in text)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            var clean = builder.ToString().TrimEnd('.', ' ');
            if (clean.Length > 80)
            {
                clean = clean.Substring(0, 80).TrimEnd('.', ' ');
            }

            if (clean.Length == 0)
            {
                clean = "Game";
            }

            return clean + " (" + id.ToString("N").Substring(0, 8) + ")";
        }

        /// <summary>True when the path exists as a directory and is readable.</summary>
        public static bool DirectoryUsable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
