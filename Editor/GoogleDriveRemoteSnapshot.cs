#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GoogleDriveImporter
{
    // A cursor is committed together with its complete snapshot, never before it.
    internal sealed class GoogleDriveRemoteSnapshot
    {
        public List<GoogleDriveFile> Nodes;
        public string Token;
        public string Account;
        public string DriveId;
        public bool FullScan;

        public static async Task<GoogleDriveRemoteSnapshot> Fetch(GoogleDriveRestClient client,
            string rootId, PresetFolderCache cache, string account, bool forceFull, int concurrency,
            Action<string> progress)
        {
            // Also validates access, trash status and movement between drives.
            GoogleDriveFile root = await client.GetFile(rootId);
            ValidateRoot(root);
            string token = null;
            List<GoogleDriveFile> nodes = null;
            if (!forceFull && cache != null && cache.ChangeAccount == account
                && cache.DriveId == root.driveId && !string.IsNullOrEmpty(cache.ChangeToken)
                && cache.Nodes != null && cache.Nodes.Count > 0)
            {
                try
                {
                    progress("Checking Drive changes: " + root.Name);
                    DriveChangeList feed = await client.GetChanges(cache.ChangeToken, root.driveId);
                    token = feed.newStartPageToken;
                    nodes = ApplyChanges(root, cache.Nodes, feed.changes);
                }
                catch (GoogleDriveRequestException error) when (error.StatusCode == 410 || error.StatusCode == 400
                    || error.StatusCode == 403 || error.StatusCode == 404)
                {
                    // An unusable cursor requires a fresh baseline.
                    nodes = null;
                }
            }

            bool full = nodes == null;
            if (full)
            {
                // Capture BEFORE listing so edits during the scan are replayed next time.
                if (token == null)
                {
                    try { token = await client.GetStartToken(root.driveId); }
                    catch (GoogleDriveRequestException error) when (error.StatusCode == 403 || error.StatusCode == 404)
                    {
                        // Non-members can have access to a shared folder without
                        // permission to read the shared drive's change log.
                        UnityEngine.Debug.LogWarning("Google Drive Importer: change log unavailable for "
                            + root.Name + "; using a full folder scan.");
                        token = string.Empty;
                    }
                }
                string driveId = root.driveId;
                root = await client.GetFile(rootId);
                ValidateRoot(root);
                if (root.driveId != driveId)
                    throw new InvalidOperationException("Drive folder moved between drives during the scan. Retry Fetch & Import.");
                nodes = new List<GoogleDriveFile> { root };
                var pending = new List<string> { rootId };
                var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
                while (pending.Count > 0)
                {
                    var next = new List<string>();
                    await EditorAsyncRunner.RunInParallel(pending, concurrency, async id =>
                    {
                        GoogleDriveFileList listing = await client.ListFilesInFolder(id, root.driveId);
                        foreach (GoogleDriveFile file in listing.Files)
                        {
                            if (!visited.Add(file.Id)) continue;
                            nodes.Add(file);
                            if (file.IsFolder) next.Add(file.Id);
                        }
                        progress("Scanning " + root.Name + ": " + nodes.Count + " entries found");
                    });
                    pending = next;
                }
            }
            return new GoogleDriveRemoteSnapshot { Nodes = nodes, Token = token, Account = account,
                DriveId = root.driveId, FullScan = full };
        }

        private static void ValidateRoot(GoogleDriveFile root)
        {
            if (root == null || !root.IsFolder || root.trashed)
                throw new InvalidOperationException("The configured Drive folder is missing, trashed or is not a folder.");
        }

        // Binary changes can be applied directly. Structural changes rebuild only
        // this root so moving a populated folder in cannot hide its descendants.
        internal static List<GoogleDriveFile> ApplyChanges(GoogleDriveFile root,
            List<GoogleDriveFile> previous, DriveChange[] changes)
        {
            var nodes = new Dictionary<string, GoogleDriveFile>(StringComparer.Ordinal);
            foreach (GoogleDriveFile file in previous)
            {
                if (file == null || string.IsNullOrEmpty(file.Id)) return null;
                nodes[file.Id] = file;
            }
            if (!nodes.ContainsKey(root.Id)) return null;
            foreach (DriveChange change in changes ?? Array.Empty<DriveChange>())
            {
                if (change.changeType == "drive" && change.driveId == root.driveId) return null;
                if (string.IsNullOrEmpty(change.fileId)) continue;
                nodes.TryGetValue(change.fileId, out var old);
                GoogleDriveFile file = change.file;
                bool enters = false;
                if (file != null && file.parents != null)
                    foreach (string parent in file.parents)
                        if (nodes.TryGetValue(parent, out var parentNode) && parentNode.IsFolder) enters = true;
                if (old == null && !enters) continue;
                if ((old != null && old.IsFolder) || (file != null && file.IsFolder)) return null;
                if (change.removed || file == null || file.trashed || !enters)
                    nodes.Remove(change.fileId);
                else
                    nodes[change.fileId] = file;
            }
            nodes[root.Id] = root;
            return new List<GoogleDriveFile>(nodes.Values);
        }
    }
}
#endif
