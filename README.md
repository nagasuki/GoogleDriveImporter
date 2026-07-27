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
4. Create `Nanuq Tools > Google Drive > Folder Preset`.
5. Add each root Drive folder to `PresetFolders`:
   - `Id`: the folder ID from its Drive URL.
   - `Path`: the destination header path, either absolute or relative to the
     Unity project, such as `Assets/GameData`.
6. Open `Tools > GoogleDrive Importer`, assign both
   assets, then click **Authorize Google Drive**.

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
