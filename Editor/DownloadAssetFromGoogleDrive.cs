#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GoogleDriveImporter
{
    [Serializable]
    public struct FolderClone
    {
        public string Id;
        public string Path;
    }

    public sealed class DownloadAssetFromGoogleDrive : EditorWindow
    {
        private const string WindowTitle = "Download Asset From Google Drive";
        private const string FingerprintKeyPrefix = "GoogleDriveImporter.Fingerprint.";

        [SerializeField]
        private PresetFolderId presetFolderId;

        [SerializeField]
        private GoogleDriveEditorSettings settings;

        private GoogleDriveEditorSettings runtimeSettings;
        private readonly List<FolderClone> folders = new List<FolderClone>();
        private readonly List<FolderScanResult> folderScans = new List<FolderScanResult>();
        private Vector2 scrollPosition;
        private bool isBusy;
        private bool hasScan;
        private bool authenticationExpanded;
        private bool wasAuthorized;
        private string status = "Ready.";
        private int newFileCount;
        private int changedFileCount;
        private int existingFileCount;
        private int skippedFileCount;

        [MenuItem("Window/Nanuq Tools/DownloadAssetFromGoogleDrive")]
        public static void ShowUpdateDataEditor()
        {
            GetWindow<DownloadAssetFromGoogleDrive>(WindowTitle).Show();
        }

        private void OnEnable()
        {
            SyncPresetFolders();
            LoadCachedScans();
            wasAuthorized = GoogleDriveAuth.HasRefreshToken;
            authenticationExpanded = !wasAuthorized || GetEffectiveSettings() == null;
        }

        private void OnDisable()
        {
            if (runtimeSettings != null)
            {
                DestroyImmediate(runtimeSettings);
                runtimeSettings = null;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(WindowTitle, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Fetch Data checks Google Drive for changes. Import Asset downloads only the folders marked as changed.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            presetFolderId = (PresetFolderId)EditorGUILayout.ObjectField(
                "Folder preset",
                presetFolderId,
                typeof(PresetFolderId),
                false);
            if (EditorGUI.EndChangeCheck())
            {
                SyncPresetFolders();
                LoadCachedScans();
            }

            EditorGUILayout.Space(4);
            DrawAuthenticationControls();
            EditorGUILayout.Space(4);
            DrawFolderControls();
        }

        private void DrawAuthenticationControls()
        {
            bool isAuthorized = GoogleDriveAuth.HasRefreshToken;
            GoogleDriveEditorSettings effectiveSettings = GetEffectiveSettings();
            if (isAuthorized != wasAuthorized)
            {
                authenticationExpanded = !isAuthorized || effectiveSettings == null;
                wasAuthorized = isAuthorized;
            }

            if (effectiveSettings == null)
                authenticationExpanded = true;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                authenticationExpanded = EditorGUILayout.Foldout(
                    authenticationExpanded,
                    isAuthorized ? "Authentication (Authorized)" : "Authentication (Required)",
                    true);

                if (!authenticationExpanded)
                    return;

                if (!isAuthorized || effectiveSettings == null)
                {
                    settings = (GoogleDriveEditorSettings)EditorGUILayout.ObjectField(
                        "OAuth settings",
                        settings,
                        typeof(GoogleDriveEditorSettings),
                        false);
                    effectiveSettings = GetEffectiveSettings();

                    if (isAuthorized && settings != null)
                        GoogleDriveAuth.StoreSettings(settings);
                }

                EditorGUILayout.LabelField(
                    isAuthorized ? "Refresh token is stored." : "No refresh token is stored.",
                    EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(isBusy || effectiveSettings == null || isAuthorized))
                {
                    if (GUILayout.Button("Authorize Google Drive"))
                        StartAuthorization();
                }

                if (isAuthorized)
                    EditorGUILayout.HelpBox("Authorized. Clear stored token before authorizing another account.", MessageType.Info);

                using (new EditorGUI.DisabledScope(isBusy || !GoogleDriveAuth.HasRefreshToken))
                {
                    if (GUILayout.Button("Clear stored token"))
                    {
                        GoogleDriveAuth.ClearStoredTokens();
                        authenticationExpanded = true;
                        wasAuthorized = false;
                        status = "Stored token cleared.";
                    }
                }

                if (effectiveSettings == null)
                {
                    EditorGUILayout.HelpBox(
                        "Create and assign a GoogleDriveEditorSettings asset before authorizing.",
                        MessageType.Warning);
                }
            }
        }

        private void DrawFolderControls()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Google Drive folders", EditorStyles.boldLabel);

                if (EditorGUIUtility.currentViewWidth < 560f)
                {
                    DrawFetchButton();
                    DrawImportButton();
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawFetchButton();
                        DrawImportButton();
                    }
                }

                if (presetFolderId == null)
                {
                    EditorGUILayout.HelpBox("Create and assign a PresetFolderId asset.", MessageType.Warning);
                    return;
                }

                if (folders.Count == 0)
                {
                    EditorGUILayout.HelpBox("The assigned preset has no folders.", MessageType.Info);
                    return;
                }

                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                foreach (var folder in folders)
                    DrawFolderStatus(folder);
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedLabel);
            if (isBusy)
                Repaint();
        }

        private void DrawFolderStatus(FolderClone folder)
        {
            FolderScanResult scan = FindScan(folder.Id);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                if (scan == null)
                {
                    EditorGUILayout.LabelField("Not scanned yet", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Google Drive ID: " + folder.Id, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Header path: " + folder.Path, EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                EditorGUILayout.LabelField(scan.RootName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Google Drive ID: " + folder.Id, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Destination: " + BuildDestinationLabel(folder.Path, scan.RootName), EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("Last Update: " + scan.LatestModifiedText, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Remote files: " + scan.Files.Count, EditorStyles.miniLabel);

                MessageType messageType = scan.IsChanged ? MessageType.Warning : MessageType.Info;
                EditorGUILayout.HelpBox(scan.StatusText, messageType);
            }
        }

        private void DrawFetchButton()
        {
            using (new EditorGUI.DisabledScope(
                isBusy
                || presetFolderId == null
                || folders.Count == 0
                || !GoogleDriveAuth.HasRefreshToken
                || GetEffectiveSettings() == null))
            {
                if (GUILayout.Button("Fetch Data", GUILayout.MinHeight(28f)))
                    StartFetchData();
            }
        }

        private void DrawImportButton()
        {
            using (new EditorGUI.DisabledScope(
                isBusy
                || !hasScan
                || !HasChangedFolders()
                || GetEffectiveSettings() == null))
            {
                if (GUILayout.Button("Import Asset", GUILayout.MinHeight(28f)))
                    StartImport();
            }
        }

        private void SyncPresetFolders()
        {
            folders.Clear();
            if (presetFolderId == null || presetFolderId.PresetFolders == null)
                return;

            foreach (var folder in presetFolderId.PresetFolders)
            {
                if (string.IsNullOrWhiteSpace(folder.Id))
                    continue;

                folders.Add(new FolderClone
                {
                    Id = folder.Id.Trim(),
                    Path = folder.Path
                });
            }
        }

        private GoogleDriveEditorSettings GetEffectiveSettings()
        {
            if (settings != null)
                return settings;

            if (runtimeSettings == null)
                runtimeSettings = GoogleDriveAuth.CreateStoredSettings();

            return runtimeSettings;
        }

        private void StartAuthorization()
        {
            GoogleDriveEditorSettings effectiveSettings = GetEffectiveSettings();
            if (effectiveSettings == null)
            {
                authenticationExpanded = true;
                status = "Assign GoogleDriveEditorSettings with a Client ID first.";
                Repaint();
                return;
            }

            GoogleDriveAuth.StoreSettings(effectiveSettings);
            isBusy = true;
            status = "Opening browser for Google authorization...";
            EditorCoroutineRunner.Start(
                GoogleDriveAuth.Authorize(effectiveSettings, message =>
                {
                    status = message;
                    Repaint();
                }),
                () =>
                {
                    status = "Google Drive authorization completed.";
                    authenticationExpanded = false;
                    wasAuthorized = true;
                    isBusy = false;
                    Repaint();
                },
                HandleRoutineError);
        }

        private void StartFetchData()
        {
            GoogleDriveEditorSettings effectiveSettings = GetEffectiveSettings();
            if (effectiveSettings == null)
            {
                authenticationExpanded = true;
                status = "Assign GoogleDriveEditorSettings with a Client ID first.";
                Repaint();
                return;
            }

            isBusy = true;
            hasScan = false;
            folderScans.Clear();
            status = "Fetching Google Drive data...";
            EditorCoroutineRunner.Start(
                ReloadPresetFolders(effectiveSettings),
                () =>
                {
                    hasScan = true;
                    int changedCount = CountChangedFolders();
                    status = changedCount == 0
                        ? "Fetch completed. All folders are up to date."
                        : string.Format("Fetch completed. {0} folder(s) need import.", changedCount);
                    isBusy = false;
                    Repaint();
                },
                HandleRoutineError);
        }

        private void StartImport()
        {
            GoogleDriveEditorSettings effectiveSettings = GetEffectiveSettings();
            if (effectiveSettings == null)
            {
                authenticationExpanded = true;
                status = "Assign GoogleDriveEditorSettings with a Client ID first.";
                Repaint();
                return;
            }

            if (!hasScan || !HasChangedFolders())
            {
                status = "Fetch Data first, or all folders are already up to date.";
                Repaint();
                return;
            }

            isBusy = true;
            ResetCounters();
            status = "Importing fetched changes...";
            EditorCoroutineRunner.Start(
                ImportChangedFolders(effectiveSettings),
                () =>
                {
                    status = string.Format(
                        "Finished. New: {0}, changed: {1}, existing: {2}, skipped: {3}.",
                        newFileCount,
                        changedFileCount,
                        existingFileCount,
                        skippedFileCount);
                    AssetDatabase.Refresh();
                    EditorUtility.ClearProgressBar();
                    isBusy = false;
                    Repaint();
                },
                HandleRoutineError);
        }

        private void HandleRoutineError(Exception exception)
        {
            status = exception.Message;
            Debug.LogException(exception);
            EditorUtility.ClearProgressBar();
            isBusy = false;
            Repaint();
        }

        private IEnumerator ReloadPresetFolders(GoogleDriveEditorSettings effectiveSettings)
        {
            using (var client = new GoogleDriveRestClient(effectiveSettings))
            {
                foreach (var folder in folders)
                {
                    status = "Scanning folder " + folder.Id + "...";
                    Repaint();

                    FolderScanResult scan = null;
                    yield return ScanPresetFolder(client, folder, result => scan = result);
                    folderScans.Add(scan);
                    SaveScanCache(scan);
                }
            }
        }

        private IEnumerator ScanPresetFolder(
            GoogleDriveRestClient client,
            FolderClone folder,
            Action<FolderScanResult> onDone)
        {
            GoogleDriveFile rootFolder = null;
            yield return client.GetFile(folder.Id, result => rootFolder = result);
            if (rootFolder == null || !rootFolder.IsFolder)
                throw new InvalidOperationException("The configured Google Drive ID is not a folder: " + folder.Id);

            var scan = new FolderScanResult(folder);
            scan.RootName = rootFolder.Name;
            scan.AddFingerprint(rootFolder, new[] { rootFolder.Name });
            scan.ConsiderModified(rootFolder.ModifiedTime);

            yield return ScanFolder(client, rootFolder.Id, new List<string>(), scan);

            scan.Fingerprint = ComputeFingerprint(scan.FingerprintParts);
            scan.PreviousFingerprint = EditorPrefs.GetString(GetFingerprintKey(folder), string.Empty);
            scan.IsChanged = string.IsNullOrEmpty(scan.PreviousFingerprint)
                || !string.Equals(scan.PreviousFingerprint, scan.Fingerprint, StringComparison.Ordinal);
            onDone?.Invoke(scan);
        }

        private IEnumerator ScanFolder(
            GoogleDriveRestClient client,
            string folderId,
            List<string> parentSegments,
            FolderScanResult scan)
        {
            GoogleDriveFileList fileList = null;
            yield return client.ListFilesInFolder(folderId, result => fileList = result);
            GoogleDriveFile[] files = fileList == null || fileList.Files == null
                ? Array.Empty<GoogleDriveFile>()
                : fileList.Files;

            foreach (GoogleDriveFile file in files)
            {
                List<string> pathSegments = AppendPathSegment(parentSegments, file.Name);
                scan.AddFingerprint(file, pathSegments.ToArray());
                scan.ConsiderModified(file.ModifiedTime);

                if (file.IsFolder)
                {
                    yield return ScanFolder(client, file.Id, pathSegments, scan);
                    continue;
                }

                if (!file.IsBinaryFile)
                {
                    scan.SkippedCount++;
                    continue;
                }

                scan.Files.Add(new RemoteAssetEntry
                {
                    Id = file.Id,
                    PathSegments = pathSegments.ToArray(),
                    Size = file.Size,
                    Md5Checksum = file.Md5Checksum
                });
            }
        }

        private IEnumerator ImportChangedFolders(GoogleDriveEditorSettings effectiveSettings)
        {
            int changedFolders = 0;
            using (var client = new GoogleDriveRestClient(effectiveSettings))
            {
                foreach (FolderScanResult scan in folderScans)
                {
                    if (!scan.IsChanged)
                        continue;

                    changedFolders++;
                    skippedFileCount += scan.SkippedCount;
                    status = "Importing " + scan.RootName + "...";
                    yield return ImportFolder(client, scan);

                    EditorPrefs.SetString(GetFingerprintKey(scan.Preset), scan.Fingerprint);
                    scan.PreviousFingerprint = scan.Fingerprint;
                    scan.IsChanged = false;
                    SaveScanCache(scan);
                }
            }

            if (changedFolders == 0)
                status = "No changed folders found.";
        }

        private IEnumerator ImportFolder(GoogleDriveRestClient client, FolderScanResult scan)
        {
            string headerPath = ResolveProjectPath(scan.Preset.Path);
            string localRoot = CombineSafePath(headerPath, scan.RootName);
            Directory.CreateDirectory(localRoot);

            for (int index = 0; index < scan.Files.Count; index++)
            {
                RemoteAssetEntry file = scan.Files[index];
                string fileName = file.PathSegments[file.PathSegments.Length - 1];
                string filePath = CombinePathSegments(localRoot, file.PathSegments);
                bool existed = File.Exists(filePath);
                bool isUnchanged = IsLocalFileUnchanged(filePath, existed, file.Size, file.Md5Checksum);

                status = string.Format("{0} ({1}/{2})", fileName, index + 1, scan.Files.Count);
                EditorUtility.DisplayProgressBar(
                    WindowTitle,
                    "Importing " + scan.RootName + ": " + fileName,
                    scan.Files.Count == 0 ? 1f : (float)index / scan.Files.Count);

                if (isUnchanged)
                {
                    existingFileCount++;
                    continue;
                }

                yield return client.DownloadFileToPath(file.Id, filePath, progress =>
                {
                    float normalizedProgress = scan.Files.Count == 0
                        ? progress
                        : (index + progress) / scan.Files.Count;
                    EditorUtility.DisplayProgressBar(
                        WindowTitle,
                        "Importing " + scan.RootName + ": " + fileName,
                        normalizedProgress);
                    Repaint();
                });

                if (existed)
                    changedFileCount++;
                else
                    newFileCount++;
            }
        }

        private static bool IsLocalFileUnchanged(string path, bool existed, long remoteSize, string remoteMd5)
        {
            if (!existed)
                return false;

            var localInfo = new FileInfo(path);
            if (localInfo.Length != remoteSize)
                return false;
            if (string.IsNullOrEmpty(remoteMd5))
                return true;

            return string.Equals(ComputeMd5(path), remoteMd5, StringComparison.OrdinalIgnoreCase);
        }

        private bool HasChangedFolders()
        {
            return CountChangedFolders() > 0;
        }

        private int CountChangedFolders()
        {
            int count = 0;
            foreach (FolderScanResult scan in folderScans)
            {
                if (scan.IsChanged)
                    count++;
            }

            return count;
        }

        private FolderScanResult FindScan(string folderId)
        {
            foreach (FolderScanResult scan in folderScans)
            {
                if (string.Equals(scan.Preset.Id, folderId, StringComparison.Ordinal))
                    return scan;
            }

            return null;
        }

        private static List<string> AppendPathSegment(List<string> parentSegments, string segment)
        {
            var result = new List<string>(parentSegments);
            result.Add(segment);
            return result;
        }

        private static string CombinePathSegments(string rootPath, string[] segments)
        {
            string result = rootPath;
            foreach (string segment in segments)
                result = CombineSafePath(result, segment);
            return result;
        }

        private static string BuildDestinationLabel(string headerPath, string rootName)
        {
            return string.IsNullOrEmpty(headerPath) ? rootName : headerPath.TrimEnd('/', '\\') + "/" + rootName;
        }

        private static string ResolveProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("The preset header path is empty.");

            if (System.IO.Path.IsPathRooted(path))
                return System.IO.Path.GetFullPath(path);

            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectPath, path));
        }

        private static string CombineSafePath(string parentPath, string name)
        {
            string safeName = SanitizeFileName(name);
            string fullParentPath = System.IO.Path.GetFullPath(parentPath).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentPath, safeName));
            if (!fullPath.StartsWith(fullParentPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Google Drive item name resolves outside the destination folder.");

            return fullPath;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "Unnamed";

            char[] invalidCharacters = System.IO.Path.GetInvalidFileNameChars();
            var result = new StringBuilder(value.Length);
            foreach (char character in value.Trim())
                result.Append(Array.IndexOf(invalidCharacters, character) >= 0 ? '_' : character);

            string sanitized = result.ToString().Trim().TrimEnd('.');
            return sanitized == "." || sanitized == ".." || sanitized.Length == 0 ? "Unnamed" : sanitized;
        }

        private static string ComputeMd5(string filePath)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filePath))
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string ComputeFingerprint(List<string> parts)
        {
            parts.Sort(StringComparer.Ordinal);
            byte[] data = Encoding.UTF8.GetBytes(string.Join("\n", parts));
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string GetFingerprintKey(FolderClone folder)
        {
            string identity = folder.Id + "\n" + folder.Path;
            var parts = new List<string> { identity };
            return FingerprintKeyPrefix + ComputeFingerprint(parts);
        }

        private void LoadCachedScans()
        {
            folderScans.Clear();
            foreach (FolderClone folder in folders)
            {
                FolderScanResult scan = LoadScanCache(folder);
                if (scan != null)
                    folderScans.Add(scan);
            }

            hasScan = folders.Count > 0 && folderScans.Count == folders.Count;
            if (hasScan)
                status = "Loaded cached folder scan.";
        }

        private static void SaveScanCache(FolderScanResult scan)
        {
            string cacheDirectory = GetCacheDirectory();
            Directory.CreateDirectory(cacheDirectory);

            var cache = new FolderScanCache
            {
                RootName = scan.RootName,
                Fingerprint = scan.Fingerprint,
                LatestModifiedTime = scan.LatestModifiedTime == DateTimeOffset.MinValue
                    ? string.Empty
                    : scan.LatestModifiedTime.ToString("O", CultureInfo.InvariantCulture),
                SkippedCount = scan.SkippedCount,
                Files = new List<RemoteAssetCache>()
            };

            foreach (RemoteAssetEntry file in scan.Files)
            {
                cache.Files.Add(new RemoteAssetCache
                {
                    Id = file.Id,
                    PathSegments = file.PathSegments,
                    Size = file.Size,
                    Md5Checksum = file.Md5Checksum
                });
            }

            File.WriteAllText(GetCachePath(scan.Preset), JsonUtility.ToJson(cache));
        }

        private static FolderScanResult LoadScanCache(FolderClone folder)
        {
            string cachePath = GetCachePath(folder);
            if (!File.Exists(cachePath))
                return null;

            try
            {
                FolderScanCache cache = JsonUtility.FromJson<FolderScanCache>(File.ReadAllText(cachePath));
                if (cache == null || string.IsNullOrEmpty(cache.RootName) || string.IsNullOrEmpty(cache.Fingerprint))
                    return null;

                var scan = new FolderScanResult(folder)
                {
                    RootName = cache.RootName,
                    Fingerprint = cache.Fingerprint,
                    PreviousFingerprint = EditorPrefs.GetString(GetFingerprintKey(folder), string.Empty),
                    SkippedCount = cache.SkippedCount
                };

                DateTimeOffset latestModifiedTime;
                if (DateTimeOffset.TryParse(cache.LatestModifiedTime, out latestModifiedTime))
                    scan.LatestModifiedTime = latestModifiedTime;

                scan.IsChanged = string.IsNullOrEmpty(scan.PreviousFingerprint)
                    || !string.Equals(scan.PreviousFingerprint, scan.Fingerprint, StringComparison.Ordinal);

                if (cache.Files != null)
                {
                    foreach (RemoteAssetCache file in cache.Files)
                    {
                        scan.Files.Add(new RemoteAssetEntry
                        {
                            Id = file.Id,
                            PathSegments = file.PathSegments,
                            Size = file.Size,
                            Md5Checksum = file.Md5Checksum
                        });
                    }
                }

                return scan;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not load Google Drive scan cache: " + exception.Message);
                return null;
            }
        }

        private static string GetCacheDirectory()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectPath, "Library", "GoogleDriveImporter");
        }

        private static string GetCachePath(FolderClone folder)
        {
            return Path.Combine(GetCacheDirectory(), GetFingerprintKey(folder) + ".json");
        }

        private void ResetCounters()
        {
            newFileCount = 0;
            changedFileCount = 0;
            existingFileCount = 0;
            skippedFileCount = 0;
        }

        private sealed class RemoteAssetEntry
        {
            public string Id;
            public string[] PathSegments;
            public long Size;
            public string Md5Checksum;
        }

        [Serializable]
        private sealed class FolderScanCache
        {
            public string RootName;
            public string Fingerprint;
            public string LatestModifiedTime;
            public int SkippedCount;
            public List<RemoteAssetCache> Files;
        }

        [Serializable]
        private sealed class RemoteAssetCache
        {
            public string Id;
            public string[] PathSegments;
            public long Size;
            public string Md5Checksum;
        }

        private sealed class FolderScanResult
        {
            public readonly FolderClone Preset;
            public readonly List<RemoteAssetEntry> Files = new List<RemoteAssetEntry>();
            public readonly List<string> FingerprintParts = new List<string>();
            public string RootName;
            public string Fingerprint;
            public string PreviousFingerprint;
            public bool IsChanged;
            public int SkippedCount;
            public DateTimeOffset LatestModifiedTime = DateTimeOffset.MinValue;

            public FolderScanResult(FolderClone preset)
            {
                Preset = preset;
            }

            public string LatestModifiedText
            {
                get
                {
                    return LatestModifiedTime == DateTimeOffset.MinValue
                        ? "-"
                        : LatestModifiedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
            }

            public string StatusText
            {
                get
                {
                    if (IsChanged)
                        return "Changed - Import Asset required";
                    return "Up to date";
                }
            }

            public void ConsiderModified(string value)
            {
                DateTimeOffset modifiedTime;
                if (DateTimeOffset.TryParse(value, out modifiedTime) && modifiedTime > LatestModifiedTime)
                    LatestModifiedTime = modifiedTime;
            }

            public void AddFingerprint(GoogleDriveFile file, string[] pathSegments)
            {
                FingerprintParts.Add(string.Join(
                    "\u001f",
                    string.Join("\u001e", pathSegments),
                    file.Id,
                    file.Name,
                    file.MimeType,
                    file.ModifiedTime,
                    file.size,
                    file.Md5Checksum));
            }
        }
    }
}
#endif
