using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SaveVault.Services
{
    /// <summary>
    /// One place where "which files belong to this target" is decided, so that the size
    /// checks during detection, the change hash and the archive writer can never disagree
    /// about the contents of a snapshot.
    /// </summary>
    public static class FileWalker
    {
        /// <summary>
        /// Files of a target. A null or empty filter means the whole subtree; otherwise the
        /// filter is a semicolon separated pattern list applied to the top level only, which
        /// is what the loose-save case needs.
        /// </summary>
        public static IEnumerable<FileInfo> Enumerate(string folder, string filter)
        {
            if (!PathTokens.DirectoryUsable(folder))
            {
                return new FileInfo[0];
            }

            if (string.IsNullOrWhiteSpace(filter))
            {
                return Recurse(folder);
            }

            var result = new List<FileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pattern in Patterns(filter))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (seen.Add(file))
                    {
                        result.Add(new FileInfo(file));
                    }
                }
            }

            return result;
        }

        public static IEnumerable<string> Patterns(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return new string[0];
            }

            return filter
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Manual recursion rather than SearchOption.AllDirectories: a single unreadable sub
        /// folder must not abort the whole walk, and noise folders are pruned on the way.
        /// </summary>
        private static IEnumerable<FileInfo> Recurse(string root)
        {
            var result = new List<FileInfo>();
            var pending = new Queue<string>();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                var folder = pending.Dequeue();

                try
                {
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        result.Add(new FileInfo(file));
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                try
                {
                    foreach (var child in Directory.GetDirectories(folder))
                    {
                        if (!ScanNoise.IsNoise(new DirectoryInfo(child).Name))
                        {
                            pending.Enqueue(child);
                        }
                    }
                }
                catch (Exception)
                {
                    // Keep the files that were already collected.
                }
            }

            return result;
        }

        /// <summary>Path of a file relative to the target folder, using backslashes.</summary>
        public static string RelativePath(string folder, string fullPath)
        {
            var root = folder.TrimEnd('\\') + "\\";
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length);
            }

            return Path.GetFileName(fullPath);
        }
    }
}
