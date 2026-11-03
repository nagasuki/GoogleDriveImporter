#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GoogleDriveImporter
{
    internal static class GoogleDriveLocalFileCache
    {
        private sealed class FileSnapshot
        {
            public bool Exists;
            public long Length;
            public long LastWriteTimeUtcTicks;
            public string Checksum;
        }

        private static readonly SemaphoreSlim FileReads = new SemaphoreSlim(4, 4);

        public static bool IsUnchanged(PresetFolderAssetCache file, string path)
        {
            if (!CanVerify(file))
                return false;
            return CompareAndRecord(file, ReadFile(path, file.Size));
        }

        public static async Task<bool> IsUnchangedAsync(PresetFolderAssetCache file, string path)
        {
            if (!CanVerify(file))
                return false;
            FileSnapshot snapshot = await ReadFileAsync(path, file.Size);
            return CompareAndRecord(file, snapshot);
        }

        private static bool CanVerify(PresetFolderAssetCache file)
        {
            return !string.IsNullOrEmpty(file.Md5Checksum)
                || (!string.IsNullOrEmpty(file.ModifiedTime)
                    && file.ModifiedTime == file.LocalModifiedTime
                    && !string.IsNullOrEmpty(file.LocalMd5Checksum));
        }

        private static bool CompareAndRecord(PresetFolderAssetCache file, FileSnapshot snapshot)
        {
            if (!snapshot.Exists || snapshot.Length != file.Size)
                return false;
            string expected = string.IsNullOrEmpty(file.Md5Checksum) ? file.LocalMd5Checksum : file.Md5Checksum;
            if (!string.Equals(snapshot.Checksum, expected, StringComparison.OrdinalIgnoreCase))
                return false;

            RecordVersion(file, snapshot);
            return true;
        }

        public static void UpdateMetadata(PresetFolderAssetCache file, string path)
        {
            RecordDownloadedFile(file, path, ReadFile(path, file.Size));
        }

        public static async Task UpdateMetadataAsync(PresetFolderAssetCache file, string path)
        {
            FileSnapshot snapshot = await ReadFileAsync(path, file.Size);
            RecordDownloadedFile(file, path, snapshot);
        }

        private static void RecordDownloadedFile(PresetFolderAssetCache file, string path, FileSnapshot snapshot)
        {
            if (!snapshot.Exists)
                throw new FileNotFoundException("Downloaded file is missing.", path);
            if (snapshot.Length != file.Size || (!string.IsNullOrEmpty(file.Md5Checksum)
                && !string.Equals(snapshot.Checksum, file.Md5Checksum, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Downloaded file does not match the fetched Drive version. Fetch Data again: " + path);

            RecordVersion(file, snapshot);
        }

        private static void RecordVersion(PresetFolderAssetCache file, FileSnapshot snapshot)
        {
            // This continuation runs on the caller's Unity context. Background
            // work only reads files, and never touches assets or serialized data.
            file.LocalLastWriteTimeUtcTicks = snapshot.LastWriteTimeUtcTicks;
            file.LocalMd5Checksum = snapshot.Checksum;
            file.LocalModifiedTime = file.ModifiedTime ?? string.Empty;
        }

        private static async Task<FileSnapshot> ReadFileAsync(string path, long expectedSize)
        {
            await FileReads.WaitAsync();
            try { return await Task.Run(() => ReadFile(path, expectedSize)); }
            finally { FileReads.Release(); }
        }

        private static FileSnapshot ReadFile(string path, long expectedSize)
        {
            var local = new FileInfo(path);
            if (!local.Exists)
                return new FileSnapshot();
            return new FileSnapshot
            {
                Exists = true,
                Length = local.Length,
                LastWriteTimeUtcTicks = local.LastWriteTimeUtc.Ticks,
                Checksum = local.Length == expectedSize ? ComputeChecksum(path) : string.Empty
            };
        }

        private static string ComputeChecksum(string path)
        {
            using (var md5 = MD5.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan))
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
#endif
