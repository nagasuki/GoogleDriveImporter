using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GoogleDriveImporter;

internal static class Program
{
    private static int checks;

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static GoogleDriveFile FileNode(string id, string parent, bool folder = false)
    {
        return new GoogleDriveFile { id = id, name = id, parents = new[] { parent },
            mimeType = folder ? "application/vnd.google-apps.folder" : "image/png" };
    }

    public static async Task Main()
    {
        var root = FileNode("root", "outside", true);
        var folder = FileNode("child", "root", true);
        var file = FileNode("image", "child");
        var previous = new List<GoogleDriveFile> { root, folder, file };
        List<GoogleDriveFile> Apply(params DriveChange[] changes) =>
            GoogleDriveRemoteSnapshot.ApplyChanges(root, previous, changes);

        Check(Apply().Count == 3, "unchanged snapshot is reusable");
        var edit = FileNode("image", "child"); edit.md5Checksum = "new";
        Check(Apply(new DriveChange { fileId = "image", file = edit }).Single(n => n.Id == "image").md5Checksum == "new",
            "binary edit updates the cached version");
        Check(file.md5Checksum == null, "delta does not mutate the previous snapshot");
        Check(Apply(new DriveChange { fileId = "new", file = FileNode("new", "child") }).Count == 4,
            "new binary in nested folder is discovered");
        Check(Apply(new DriveChange { fileId = "image", removed = true }).Count == 2,
            "removed binary leaves remote snapshot");
        Check(Apply(new DriveChange { fileId = "image", file = FileNode("image", "outside") }).Count == 2,
            "binary moved out leaves remote snapshot");
        Check(Apply(new DriveChange { fileId = "other", file = FileNode("other", "outside") }).Count == 3,
            "unrelated changes do not invalidate root");
        Check(Apply(new DriveChange { fileId = "incoming", file = FileNode("incoming", "child", true) }) == null,
            "populated folder moved in requires full subtree listing");
        Check(Apply(new DriveChange { fileId = "child", removed = true }) == null,
            "removed folder invalidates its descendants");
        var renamed = FileNode("child", "root", true); renamed.name = "renamed";
        Check(Apply(new DriveChange { fileId = "child", file = renamed }) == null,
            "folder rename refreshes descendant paths");
        var sharedRoot = FileNode("root", "outside", true); sharedRoot.driveId = "shared";
        Check(GoogleDriveRemoteSnapshot.ApplyChanges(sharedRoot, previous,
            new[] { new DriveChange { changeType = "drive", driveId = "shared", removed = true } }) == null,
            "shared drive access change invalidates snapshot");

        string path = Path.Combine(Path.GetTempPath(), "gdrive-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            await System.IO.File.WriteAllTextAsync(path, "ABCD");
            var metadata = new PresetFolderAssetCache { Size = 4, ModifiedTime = "v1" };
            await GoogleDriveLocalFileCache.UpdateMetadataAsync(metadata, path);
            Check(await GoogleDriveLocalFileCache.IsUnchangedAsync(metadata, path), "fast check accepts verified file");
            Check(await GoogleDriveLocalFileCache.IsUnchangedAsync(metadata, path, true), "full check accepts matching bytes");
            long ticks = metadata.LocalLastWriteTimeUtcTicks;
            await System.IO.File.WriteAllTextAsync(path, "WXYZ");
            System.IO.File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc));
            Check(await GoogleDriveLocalFileCache.IsUnchangedAsync(metadata, path), "fast mode has documented same-size/time limitation");
            Check(!await GoogleDriveLocalFileCache.IsUnchangedAsync(metadata, path, true), "Verify All detects same-size/time content edit");
            metadata.ModifiedTime = "v2";
            Check(!await GoogleDriveLocalFileCache.IsUnchangedAsync(metadata, path), "new remote version bypasses metadata shortcut");
            System.IO.File.Delete(path);
            Check(!await GoogleDriveLocalFileCache.IsUnchangedAsync(metadata, path), "missing local file needs download");
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }

        // Verify path rebuilding after a delta, without invoking editor APIs.
        var build = typeof(GoogleDriveImporter.GoogleDriveImporter).GetMethod("BuildScan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var snapshot = new GoogleDriveRemoteSnapshot { Nodes = Apply(new DriveChange { fileId = "image", file = edit }) };
        object scan = build.Invoke(null, new object[] { new FolderClone { Id = "root" }, snapshot, null });
        var files = (List<PresetFolderAssetCache>)scan.GetType().GetField("Files").GetValue(scan);
        Check(files.Single().PathSegments.SequenceEqual(new[] { "child", "image" }), "incremental snapshot rebuilds nested import path");
        Console.WriteLine($"{checks} checks passed.");
    }
}
