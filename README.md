# GoogleDriveImporter

Editor-only Google Drive asset importer for Unity.

The package calls the Google Drive REST API directly with Unity's built-in
`UnityWebRequest` and runs all editor work through `IEnumerator`. It does not
depend on `UnityGoogleDrive`, UniTask or other third-party packages.

## Setup

1. In Google Cloud Console, enable the Google Drive API.
2. Create an OAuth client with application type **Desktop app**.
3. In Unity, create `Google Drive Importer > Editor Settings` and fill in
   the OAuth client ID. The client secret is optional for Desktop clients.
4. Create `Google Drive Importer > Folder Preset`.
5. Add a `Groups` entry for each destination section:
   - Group `Path`: the destination header path, either absolute or relative to
     the Unity project, such as `Assets/GameData`.
   - Group `Id`: optional. If set, the Drive folder with this ID is imported
     below the group path.
   - Group `Children`: add any additional Drive folders. Each child has its own
     `Id` and optional `Path`. A blank child path uses the group path; a relative
     path such as `Characters` creates a subfolder under the group path; a path
     beginning with `Assets/` or an absolute path overrides the group path.
   - The Drive folder's own name is created below its resolved destination path.
6. Open `Tools > GoogleDrive Importer`, assign both
   assets, then click **Authorize Google Drive**.

Existing assets using the flat `PresetFolders` list remain supported. New
folder mappings can be added under `Groups` to keep related destinations
together.

When Odin Inspector is installed and `ODIN_INSPECTOR` is enabled, destination
path fields also use Odin's `[FolderPath]` picker. The picker is relative to the
Unity project by default, and paths do not need to exist before importing.

If the project has Odin Inspector installed and the `ODIN_INSPECTOR` scripting
define enabled, the tool opens as an `OdinEditorWindow` and draws its custom UI
through Odin's inspector pipeline. Without Odin, it automatically uses Unity's
built-in `EditorWindow`.

## Installation

In Unity Package Manager, choose **Add package from git URL** and enter:

`https://github.com/nagasuki/GoogleDriveImporter.git`

## Current workflow

1. Click **Fetch Data** to scan the Drive folders and review their names,
   Last Update, and change status. This step does not download assets.
2. Click **Import Asset** to import only folders marked as changed by the
   latest fetch. If everything is up to date, the button remains disabled.

Fetch results are stored in hidden serialized cache fields on the assigned
`PresetFolderId` asset, so closing and reopening the tool keeps the folder
names, Last Update, change status, and scanned file list. The asset stores both
the latest fetched fingerprint and the last successfully imported fingerprint.
Commit the `PresetFolderId` asset to version control so teammates can share the
same fetched snapshot and do not download the same update unnecessarily.
Import uses the latest fetched snapshot; run Fetch Data again when you want to
check Drive for new changes.

Fetch Data also checks the local destination. If the imported root folder or
any previously imported binary file is missing, the folder is marked as changed
so Import Asset can restore it even when the Google Drive fingerprint is
unchanged.

Changed binary files are downloaded in parallel. The default is 4 simultaneous
downloads; adjust `MaxParallelDownloads` on the `GoogleDriveEditorSettings`
asset from 1 to 8 to balance speed against network or Google Drive API
throttling.

The first authorization opens the system browser and receives the OAuth result
through a temporary localhost callback. Tokens are stored in Unity EditorPrefs;
the OAuth client settings are remembered there as well, so the settings field
can remain hidden after authorization. Legacy `PlayerPrefs` token keys are
also read for migration compatibility.

The importer lists recursively, follows pagination, supports My Drive and
Shared Drive items, uses the Google Drive folder/file names as-is, downloads
binary files, and skips Google Workspace files. The selected root folder is
created below the configured header path. Existing files are compared by size
and, when available, MD5 checksum. Fetch Data also computes a recursive snapshot
so additions, removals, renames, and nested file changes are detected even if
the root folder's own `modifiedTime` did not change. Local files are not
deleted automatically when a remote file is removed.

If an existing authorized setup was created before OAuth settings were cached,
expand **Authentication** and assign the `GoogleDriveEditorSettings` asset once.
After that, the settings field can remain hidden while the stored authorization
is used normally.

## API references

- https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list
- https://developers.google.com/workspace/drive/api/guides/manage-downloads
- https://developers.google.com/identity/protocols/oauth2
