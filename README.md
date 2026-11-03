# GoogleDriveImporter

Editor-only Google Drive asset importer for Unity.

The package calls the Google Drive REST API directly with Unity's built-in
`UnityWebRequest`. When UniTask 2.5+ is installed (through UPM or under Assets),
async operations automatically use its awaiters. Without UniTask, they use
`System.Threading.Tasks.Task` and `async/await`. Downloads, scans and OAuth do
not use coroutines or `IEnumerator`. No scripting defines or mandatory
third-party dependencies are required.

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
   assets, then click **Authorize Google Drive**. The settings reference is saved
   on the folder preset. Commit both assets and their `.meta` files to share the
   authorization with your team.

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

Click **Fetch & Import** to scan the Drive folders, check local file contents,
and immediately import new or changed assets. Matching files are skipped. If
everything is up to date, the operation completes without downloading anything.
The button remains available for the next check and is disabled while busy.

One Editor progress bar runs from 0 to 100%, with the current stage and completed
folder/file counts. Scanning uses 0-20%, local checks use 20-30%, and downloads
and checksum verification use 30-95%, weighted by file size. The final stage saves
the cache and imports assets into Unity. It reaches 100% after the
whole operation finishes; the tool keeps the final percentage visible. The
popup closes on completion, failure, window close, or script reload.

Fetch results are stored in hidden serialized cache fields on the assigned
`PresetFolderId` asset, so closing and reopening the tool keeps the folder
names, Last Update, change status, and scanned file list. The asset stores both
the latest fetched fingerprint and the last successfully imported fingerprint.
Commit the `PresetFolderId` asset to version control so teammates can share the
same fetched snapshot and do not download the same update unnecessarily.
Each click refreshes the Drive snapshot before importing, so a cached scan never
causes new Drive changes to be overlooked.

Fetch & Import also checks the actual bytes of local files against Drive's MD5
checksums. Missing files, changed bytes (including same-size edits), and changed
Drive versions mark the folder for import. Matching files are skipped even if
another file in the same folder changed. Checksums are computed by streaming the
local bytes, without downloading another copy or retaining a whole image in RAM.
During Fetch & Import, file reads and hashing run on up to four background workers;
cache updates and Unity APIs resume on the editor thread.

For a binary file without a Drive checksum, the importer records its remote
modification time and a checksum of the downloaded bytes. It skips that file only
when the remote version and local bytes still match; size alone is insufficient.
Old cache entries without this information are downloaded once to establish it.

Changed binary files are downloaded in parallel. The default is 8 simultaneous
downloads; adjust `MaxParallelDownloads` on the `GoogleDriveEditorSettings`
asset from 1 to 32 to balance speed against network or Google Drive API
throttling. Existing settings keep their configured value. Larger files start
first, and finished slots are refilled when the previous operation completes. The importer
requests one asset refresh after a completed import.

Completed files keep their verified metadata even if another download fails,
so retrying the import skips those files. A downloaded file must match the
fetched size/checksum before it is recorded as complete.

The first authorization opens the system browser and receives the OAuth result
through a temporary localhost callback. Access tokens, refresh tokens, and expiry
are serialized into the `GoogleDriveEditorSettings` asset and saved immediately
after authorization or refresh. A teammate using the shared preset/settings can
fetch and import immediately; expired access tokens refresh automatically using
the shared refresh token. All users of that asset access Drive as the authorized
Google account, so share it only with teammates who should have that access.

Existing EditorPrefs (or legacy PlayerPrefs) tokens migrate automatically after
scripts reload, or when opening the importer, into a settings asset with the same
cached client ID and scope.
Migration leaves the old local entries available to other projects. Each asset
records that migration or authorization has occurred, so clearing it never
restores an old local token. If there is only one settings asset in the project,
the importer selects it automatically; otherwise
assign it under **Authentication** or on the folder preset.

The importer lists recursively, follows pagination, supports My Drive and
Shared Drive items, uses the Google Drive folder/file names as-is, downloads
binary files, and skips Google Workspace files. The selected root folder is
created below the configured header path. Existing files are compared by size
and, when available, MD5 checksum. Fetch & Import also computes a recursive snapshot
so additions, removals, renames, and nested file changes are detected even if
the root folder's own `modifiedTime` did not change. Local files are not
deleted automatically when a remote file is removed.

**Clear stored token** removes authorization from the assigned settings asset.
Changing its client ID or scope requires authorization again. If an older local
setup has no cached client ID, authorize the settings asset once to establish
the shared authorization.

To install UniTask through Package Manager's **Add package from git URL**, use:

`https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.11`

The optional integration uses UniTask's public `WaitUntil`, `ToUniTask` and
`AsTask` APIs, resolved once per domain reload. The Task fallback awaits
`AsyncOperation.completed` directly, so a completed request can immediately start
the next queued file. A bounded worker pool uses `Task.WhenAll` to observe all
in-flight downloads, including when a request fails. Unity API calls and UI
callbacks stay on the editor thread. Pending waits are canceled before assembly
reload or editor shutdown, allowing requests and temporary files to be cleaned up.

## API references

- https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list
- https://developers.google.com/workspace/drive/api/guides/manage-downloads
- https://developers.google.com/identity/protocols/oauth2
