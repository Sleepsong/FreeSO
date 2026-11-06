using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.Common.Utils
{
    public static class PathCaseTools
    {
        /// <summary>
        /// Resolves a file path case-insensitively on Linux/macOS.
        /// On Windows, simply checks if the file exists.
        /// (The file as written is tried first, and the case-insensitive search starts from the
        /// deepest folder that exists as written, since folders above the app's storage, like /storage/emulated on
        /// Android, can't be listed.)
        /// </summary>
        public static string Insensitive(string file)
        {
            if (string.IsNullOrEmpty(file))
                return null;

            // as written (and any case-insensitive file system, like Windows or Android's shared storage)
            if (File.Exists(file))
                return file;
            if (OperatingSystem.IsWindows())
                return null;

            file = file.Replace('\\', '/');

            // the parts below the deepest folder that exists as written are matched ignoring case
            var rest = new Stack<string>();
            rest.Push(Path.GetFileName(file));
            var dir = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                rest.Push(Path.GetFileName(dir));
                dir = Path.GetDirectoryName(dir);
            }
            var resolved = string.IsNullOrEmpty(dir) ? "." : dir;

            foreach (var part in rest)
            {
                if (string.IsNullOrEmpty(part))
                    continue;
                if (!Directory.Exists(resolved))
                    return null;

                try
                {
                    var entries = Directory.GetFileSystemEntries(resolved);
                    var match = entries.FirstOrDefault(e =>
                        Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                        return null;

                    resolved = match;
                }
                catch
                {
                    return null;
                }
            }

            return File.Exists(resolved) ? resolved : null;
        }
    }
}
