using System;
using System.IO;

namespace SharpMemories
{
    public static class FileHelpers
    {
        public static string GetAvailablePath(string folder, string baseName, string extension)
        {
            var safe = MakeSafeFilename(baseName ?? "").Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(safe)) safe = "screenshot";
            for (int i = 0; i < 100000; i++)
            {
                string candidate = Path.Combine(folder, safe + (i == 0 ? "" : "_" + i) + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("Unable to allocate unique screenshot filename");
        }

        public static string MakeSafeFilename(string name)
        {
            // Remove all invalid chars for a cleaner name
            var arr = (name ?? "").ToCharArray();
            arr = Array.FindAll<char>(arr, (c => Array.IndexOf<char>(Path.GetInvalidFileNameChars(), c) < 0));
            return new string(arr);
        }
    }
}
