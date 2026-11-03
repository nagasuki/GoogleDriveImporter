#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
#endif

namespace GoogleDriveImporter
{
    [Serializable]
    public struct FolderClone
    {
        public string Id;
        public string Path;

        [NonSerialized]
        public PresetFolder Source;
    }

    public sealed class GoogleDriveImporter :
#if ODIN_INSPECTOR
        OdinEditorWindow
#else
        EditorWindow
#endif
    {
        private const string WindowTitle = "GoogleDrive Importer";

        [SerializeField, HideInInspector]
        private PresetFolderId presetFolderId;

        [SerializeField, HideInInspector]
        private GoogleDriveEditorSettings settings;

        private readonly List<FolderClone> folders = new List<FolderClone>();
        private readonly List<FolderScanResult> folderScans = new List<FolderScanResult>();
        private Vector2 scrollPosition;
        private bool isBusy;
        private bool hasScan;
        private bool authenticationExpanded;
        private bool wasAuthorized;
        private bool searchedSettings;
        private string status = "Ready.";
        private int newFileCount;
        private int changedFileCount;
        private int existingFileCount;
        private int skippedFileCount;

        [MenuItem("Tools/GoogleDrive Importer")]
        public static void ShowUpdateDataEditor()
        {
            GetWindow<GoogleDriveImporter>(WindowTitle).Show();
        }

        private void InitializeImporter()
        {
            searchedSettings = false;
            SyncPresetFolders();
            LoadCachedScans();
            wasAuthorized = GoogleDriveAuth.HasRefreshToken(GetEffectiveSettings());
            authenticationExpanded = !wasAuthorized || GetEffectiveSettings() == null;
        }

#if ODIN_INSPECTOR
        protected override void OnEnable()
        {
            base.OnEnable();
            InitializeImporter();
        }

        [OnInspectorGUI]
        private void DrawOdinWindow()
        {
            DrawWindowGUI();
        }
#else
        private void OnEnable()
        {
            InitializeImporter();
        }

        private void OnGUI()
        {
            DrawWindowGUI();
        }
#endif

        private void DrawWindowGUI()
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
                if (presetFolderId != null && presetFolderId.Settings != null)
                    settings = presetFolderId.Settings;
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
            GoogleDriveEditorSettings effectiveSettings = GetEffectiveSettings();
            bool isAuthorized = GoogleDriveAuth.HasRefreshToken(effectiveSettings);
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

                using (new EditorGUI.DisabledScope(isBusy))
                {
                    EditorGUI.BeginChangeCheck();
                    settings = (GoogleDriveEditorSettings)EditorGUILayout.ObjectField(
                        "OAuth settings",
                        effectiveSettings,
                        typeof(GoogleDriveEditorSettings),
                        false);
                    if (EditorGUI.EndChangeCheck())
                        SavePresetSettings(settings);
                    effectiveSettings = GetEffectiveSettings();
                    isAuthorized = GoogleDriveAuth.HasRefreshToken(effectiveSettings);
                }

                EditorGUILayout.LabelField(
                    isAuthorized ? "Authorization is saved in the settings asset." : "No refresh token is stored in the settings asset.",
                    EditorStyles.miniLabel);

                using (new EditorGUI.DisabledScope(isBusy || effectiveSettings == null || isAuthorized))
                {
                    if (GUILayout.Button("Authorize Google Drive"))
                        StartAuthorization();
                }

                if (isAuthorized)
                    EditorGUILayout.HelpBox("Authorized. Clear stored token before authorizing another account.", MessageType.Info);

                using (new EditorGUI.DisabledScope(isBusy || !isAuthorized))
                {
                    if (GUILayout.Button("Clear stored token"))
                    {
                        GoogleDriveAuth.ClearStoredTokens(effectiveSettings);
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
            FolderScanResult scan = FindScan(folder);
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
                || !GoogleDriveAuth.HasRefreshToken(GetEffectiveSettings())
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
            if (presetFolderId == null)
                return;

            if (presetFolderId.Groups != null)
            {
                foreach (PresetFolderGroup group in presetFolderId.Groups)
                {
                    if (group == null)
                        continue;

                    AddFolderClone(group, group.Path);

                    if (group.Children == null)
                        continue;

                    foreach (PresetFolder child in group.Children)
                    {
                        if (child == null)
                            continue;

                        AddFolderClone(child, ResolveGroupedPath(group.Path, child.Path));
                    }
                }
            }

            // Existing assets keep their original flat mappings and remain importable.
            if (presetFolderId.PresetFolders == null)
                return;

            foreach (PresetFolder folder in presetFolderId.PresetFolders)
                AddFolderClone(folder, folder == null ? null : folder.Path);
        }

        private void AddFolderClone(PresetFolder source, string path)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Id))
                return;

            string folderId = source.Id.Trim();
            foreach (FolderClone existing in folders)
            {
                if (string.Equals(existing.Id, folderId, StringComparison.Ordinal)
                    && string.Equals(existing.Path ?? string.Empty, path ?? string.Empty, StringComparison.Ordinal))
                    return;
            }

            folders.Add(new FolderClone
            {
                Id = folderId,
                Path = path,
                Source = source
            });
        }

        private static string ResolveGroupedPath(string groupPath, string childPath)
        {
            string child = (childPath ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(child))
                return groupPath;

            string normalizedChild = child.Replace('\\', '/');
            bool isAssetsPath = string.Equals(normalizedChild, "Assets", StringComparison.OrdinalIgnoreCase)
                || normalizedChild.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            if (System.IO.Path.IsPathRooted(child) || isAssetsPath)
                return child;

            string parent = (groupPath ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(parent))
                return child;

            return parent.TrimEnd('/', '\\') + "/" + child.TrimStart('/', '\\');
        }

        private GoogleDriveEditorSettings GetEffectiveSettings()
        {
            if (presetFolderId != null && presetFolderId.Settings != null)
                settings = presetFolderId.Settings;

            if (settings == null && !searchedSettings)
            {
                searchedSettings = true;
                string[] candidates = AssetDatabase.FindAssets("t:GoogleDriveEditorSettings");
                if (candidates.Length == 1)
                    settings = AssetDatabase.LoadAssetAtPath<GoogleDriveEditorSettings>(AssetDatabase.GUIDToAssetPath(candidates[0]));
            }

            if (settings != null)
            {
                GoogleDriveAuth.MigrateStoredTokens(settings);
                if (presetFolderId != null && presetFolderId.Settings == null)
                    SavePresetSettings(settings);
            }
            return settings;
        }

        private void SavePresetSettings(GoogleDriveEditorSettings value)
        {
            if (presetFolderId == null || presetFolderId.Settings == value)
                return;
            presetFolderId.Settings = value;
            EditorUtility.SetDirty(presetFolderId);
            AssetDatabase.SaveAssetIfDirty(presetFolderId);
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

            isBusy = true;
            status = "Opening browser for Google authorization...";
            EditorAsyncRunner.Start(
                () => GoogleDriveAuth.Authorize(effectiveSettings, message =>
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
            EditorAsyncRunner.Start(
                () => ReloadPresetFolders(effectiveSettings),
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
            EditorAsyncRunner.Start(
                () => ImportChangedFolders(effectiveSettings),
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

        private async Task ReloadPresetFolders(GoogleDriveEditorSettings effectiveSettings)
        {
            using (var client = new GoogleDriveRestClient(effectiveSettings))
            {
                foreach (var folder in folders)
                {
                    status = "Scanning folder " + folder.Id + "...";
                    Repaint();

                    FolderScanResult scan = await ScanPresetFolder(client, folder);
                    folderScans.Add(scan);
                    SavePresetFolderCache(scan);
                }
            }
        }

        private async Task<FolderScanResult> ScanPresetFolder(
            GoogleDriveRestClient client,
            FolderClone folder)
        {
            GoogleDriveFile rootFolder = await client.GetFile(folder.Id);
            if (rootFolder == null || !rootFolder.IsFolder)
                throw new InvalidOperationException("The configured Google Drive ID is not a folder: " + folder.Id);

            var scan = new FolderScanResult(folder);
            scan.RootName = rootFolder.Name;
            scan.AddFingerprint(rootFolder, new[] { rootFolder.Name });
            scan.ConsiderModified(rootFolder.ModifiedTime);

            await ScanFolder(client, rootFolder.Id, new List<string>(), scan);

            scan.FetchedFingerprint = ComputeFingerprint(scan.FingerprintParts);
            PresetFolderCache cache = GetPresetFolderCache(folder);
            if (!IsCacheForFolder(cache, folder))
                cache = null;
            scan.ImportedFingerprint = cache == null ? string.Empty : cache.ImportedFingerprint;
            ApplyCachedLocalMetadata(scan, cache);
            scan.IsChanged = string.IsNullOrEmpty(scan.ImportedFingerprint)
                || !string.Equals(scan.ImportedFingerprint, scan.FetchedFingerprint, StringComparison.Ordinal)
                || await HasLocalChangesAsync(scan);
            return scan;
        }

        private static bool HasLocalChanges(FolderScanResult scan)
        {
            string headerPath = ResolveProjectPath(scan.Preset.Path);
            string localRoot = CombineSafePath(headerPath, scan.RootName);
            if (!Directory.Exists(localRoot))
                return true;

            foreach (PresetFolderAssetCache file in scan.Files)
            {
                string filePath = CombinePathSegments(localRoot, file.PathSegments);
                if (!GoogleDriveLocalFileCache.IsUnchanged(file, filePath))
                    return true;
            }

            return false;
        }

        private static async Task<bool> HasLocalChangesAsync(FolderScanResult scan)
        {
            string localRoot = CombineSafePath(ResolveProjectPath(scan.Preset.Path), scan.RootName);
            if (!Directory.Exists(localRoot))
                return true;
            foreach (PresetFolderAssetCache file in scan.Files)
            {
                string path = CombinePathSegments(localRoot, file.PathSegments);
                if (!await GoogleDriveLocalFileCache.IsUnchangedAsync(file, path))
                    return true;
            }
            return false;
        }

        private static void ApplyCachedLocalMetadata(FolderScanResult scan, PresetFolderCache cache)
        {
            if (cache == null || cache.Files == null || cache.Files.Count == 0)
                return;

            var cachedFiles = new Dictionary<string, PresetFolderAssetCache>(StringComparer.Ordinal);
            foreach (PresetFolderAssetCache file in cache.Files)
            {
                if (file != null && !string.IsNullOrEmpty(file.Id))
                    cachedFiles[file.Id] = file;
            }

            foreach (PresetFolderAssetCache file in scan.Files)
            {
                PresetFolderAssetCache cachedFile;
                if (!cachedFiles.TryGetValue(file.Id, out cachedFile))
                    continue;

                file.LocalLastWriteTimeUtcTicks = cachedFile.LocalLastWriteTimeUtcTicks;
                file.LocalMd5Checksum = cachedFile.LocalMd5Checksum;
                file.LocalModifiedTime = cachedFile.LocalModifiedTime;
            }
        }

        private async Task ScanFolder(
            GoogleDriveRestClient client,
            string folderId,
            List<string> parentSegments,
            FolderScanResult scan)
        {
            GoogleDriveFileList fileList = await client.ListFilesInFolder(folderId);
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
                    await ScanFolder(client, file.Id, pathSegments, scan);
                    continue;
                }

                if (!file.IsBinaryFile)
                {
                    scan.SkippedCount++;
                    continue;
                }

                scan.Files.Add(new PresetFolderAssetCache
                {
                    Id = file.Id,
                    PathSegments = pathSegments.ToArray(),
                    Size = file.Size,
                    Md5Checksum = file.Md5Checksum,
                    ModifiedTime = file.ModifiedTime
                });
            }
        }

        private async Task ImportChangedFolders(GoogleDriveEditorSettings effectiveSettings)
        {
            int changedFolders = 0;
            int maxParallelDownloads = ResolveMaxParallelDownloads(effectiveSettings);
            var changedScans = new List<FolderScanResult>();
            var allWorkItems = new List<ImportFileWork>();
            var pendingDownloads = new List<ImportFileWork>();
            try
            {
                using (var client = new GoogleDriveRestClient(effectiveSettings))
                {
                    foreach (FolderScanResult scan in folderScans)
                    {
                        if (!scan.IsChanged)
                            continue;

                        changedFolders++;
                        changedScans.Add(scan);
                        skippedFileCount += scan.SkippedCount;
                        status = "Preparing " + scan.RootName + "...";
                        await PrepareImportFolder(scan, allWorkItems, pendingDownloads);
                    }

                    var progressState = new ImportProgressState();
                    UpdateImportProgress(allWorkItems, progressState, true);
                    if (pendingDownloads.Count > 0)
                    {
                        pendingDownloads.Sort((left, right) => right.File.Size.CompareTo(left.File.Size));
                        await EditorAsyncRunner.RunInParallel(pendingDownloads, maxParallelDownloads,
                            work => DownloadFile(client, work, allWorkItems, progressState));
                        UpdateImportProgress(allWorkItems, progressState, true);
                    }

                    foreach (FolderScanResult scan in changedScans)
                    {
                        scan.ImportedFingerprint = scan.FetchedFingerprint;
                        scan.IsChanged = false;
                        SavePresetFolderCache(scan);
                    }
                }
            }
            finally
            {
                // Entries are shared with the serialized cache. Keep completed
                // files reusable even if another request interrupted the batch.
                if (presetFolderId != null)
                {
                    EditorUtility.SetDirty(presetFolderId);
                    AssetDatabase.SaveAssetIfDirty(presetFolderId);
                }
            }

            if (changedFolders == 0)
                status = "No changed folders found.";
        }

        private async Task PrepareImportFolder(
            FolderScanResult scan,
            List<ImportFileWork> allWorkItems,
            List<ImportFileWork> pendingDownloads)
        {
            string headerPath = ResolveProjectPath(scan.Preset.Path);
            string localRoot = CombineSafePath(headerPath, scan.RootName);
            Directory.CreateDirectory(localRoot);

            for (int index = 0; index < scan.Files.Count; index++)
            {
                PresetFolderAssetCache file = scan.Files[index];
                string fileName = file.PathSegments[file.PathSegments.Length - 1];
                string filePath = CombinePathSegments(localRoot, file.PathSegments);
                bool existed = File.Exists(filePath);
                bool isUnchanged = await GoogleDriveLocalFileCache.IsUnchangedAsync(file, filePath);

                var work = new ImportFileWork
                {
                    Scan = scan,
                    File = file,
                    FileName = fileName,
                    FilePath = filePath,
                    Existed = existed,
                    Progress = isUnchanged ? 1f : 0f
                };
                allWorkItems.Add(work);

                if (isUnchanged)
                {
                    existingFileCount++;
                    continue;
                }

                pendingDownloads.Add(work);
            }
        }

        private async Task DownloadFile(
            GoogleDriveRestClient client,
            ImportFileWork work,
            List<ImportFileWork> allWorkItems,
            ImportProgressState progressState)
        {
            status = "Downloading " + work.Scan.RootName + ": " + work.FileName + "...";
            await client.DownloadFileToPath(work.File.Id, work.FilePath, progress =>
            {
                work.Progress = progress;
                UpdateImportProgress(allWorkItems, progressState, false);
            });

            work.Progress = 1f;
            await GoogleDriveLocalFileCache.UpdateMetadataAsync(work.File, work.FilePath);
            if (work.Existed)
                changedFileCount++;
            else
                newFileCount++;
            UpdateImportProgress(allWorkItems, progressState, false);
        }

        private void UpdateImportProgress(
            List<ImportFileWork> workItems,
            ImportProgressState progressState,
            bool force)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && progressState.LastUpdateTime >= 0d && now - progressState.LastUpdateTime < 0.1d)
                return;

            progressState.LastUpdateTime = now;
            float progress = 1f;
            if (workItems.Count > 0)
            {
                progress = 0f;
                for (int index = 0; index < workItems.Count; index++)
                    progress += Mathf.Clamp01(workItems[index].Progress);
                progress /= workItems.Count;
            }

            EditorUtility.DisplayProgressBar(
                WindowTitle,
                "Importing Google Drive assets (" + Mathf.RoundToInt(progress * 100f) + "%)",
                progress);
            Repaint();
        }

        private static int ResolveMaxParallelDownloads(GoogleDriveEditorSettings settings)
        {
            int configured = settings == null
                ? GoogleDriveEditorSettings.DefaultMaxParallelDownloads
                : settings.MaxParallelDownloads;
            if (configured <= 0)
                configured = GoogleDriveEditorSettings.DefaultMaxParallelDownloads;

            return Mathf.Clamp(
                configured,
                1,
                GoogleDriveEditorSettings.MaxSupportedParallelDownloads);
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

        private FolderScanResult FindScan(FolderClone folder)
        {
            foreach (FolderScanResult scan in folderScans)
            {
                if (string.Equals(scan.Preset.Id, folder.Id, StringComparison.Ordinal)
                    && string.Equals(scan.Preset.Path ?? string.Empty, folder.Path ?? string.Empty, StringComparison.Ordinal))
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

        private static string ComputeFingerprint(List<string> parts)
        {
            parts.Sort(StringComparer.Ordinal);
            byte[] data = Encoding.UTF8.GetBytes(string.Join("\n", parts));
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private void LoadCachedScans()
        {
            folderScans.Clear();
            foreach (FolderClone folder in folders)
            {
                PresetFolderCache cache = GetPresetFolderCache(folder);
                FolderScanResult scan = CreateScanFromCache(folder, cache);
                if (scan != null)
                    folderScans.Add(scan);
            }

            hasScan = folders.Count > 0 && folderScans.Count == folders.Count;
            if (hasScan)
                status = "Loaded cached folder scan.";
        }

        private FolderScanResult CreateScanFromCache(FolderClone folder, PresetFolderCache cache)
        {
            if (!IsCacheForFolder(cache, folder)
                || string.IsNullOrEmpty(cache.RootName)
                || string.IsNullOrEmpty(cache.FetchedFingerprint))
                return null;

            var scan = new FolderScanResult(folder)
            {
                RootName = cache.RootName,
                FetchedFingerprint = cache.FetchedFingerprint,
                ImportedFingerprint = cache.ImportedFingerprint,
                SkippedCount = cache.SkippedCount
            };

            DateTimeOffset latestModifiedTime;
            if (DateTimeOffset.TryParse(cache.LatestModifiedTime, out latestModifiedTime))
                scan.LatestModifiedTime = latestModifiedTime;

            if (cache.Files != null)
            {
                foreach (PresetFolderAssetCache file in cache.Files)
                {
                    if (file != null)
                        scan.Files.Add(file);
                }
            }

            scan.IsChanged = string.IsNullOrEmpty(scan.ImportedFingerprint)
                || !string.Equals(scan.ImportedFingerprint, scan.FetchedFingerprint, StringComparison.Ordinal)
                || HasLocalChanges(scan);

            return scan;
        }

        private static bool IsCacheForFolder(PresetFolderCache cache, FolderClone folder)
        {
            return cache != null
                && string.Equals(cache.FolderId, folder.Id, StringComparison.Ordinal)
                && string.Equals(cache.HeaderPath ?? string.Empty, folder.Path ?? string.Empty, StringComparison.Ordinal);
        }

        private PresetFolderCache GetPresetFolderCache(FolderClone folder)
        {
            if (folder.Source != null)
            {
                if (folder.Source.Cache == null)
                    folder.Source.Cache = new PresetFolderCache();
                return folder.Source.Cache;
            }

            if (presetFolderId == null)
                return null;

            if (presetFolderId.Groups != null)
            {
                foreach (PresetFolderGroup group in presetFolderId.Groups)
                {
                    if (group == null)
                        continue;

                    PresetFolderCache groupCache = FindMatchingCache(group, folder);
                    if (groupCache != null)
                        return groupCache;

                    if (group.Children == null)
                        continue;

                    foreach (PresetFolder child in group.Children)
                    {
                        PresetFolderCache childCache = FindMatchingCache(child, folder);
                        if (childCache != null)
                            return childCache;
                    }
                }
            }

            if (presetFolderId.PresetFolders != null)
            {
                foreach (PresetFolder source in presetFolderId.PresetFolders)
                {
                    PresetFolderCache cache = FindMatchingCache(source, folder);
                    if (cache != null)
                        return cache;
                }
            }

            return null;
        }

        private static PresetFolderCache FindMatchingCache(PresetFolder source, FolderClone folder)
        {
            if (source == null
                || !string.Equals(source.Id, folder.Id, StringComparison.Ordinal))
                return null;

            if (source.Cache == null)
                source.Cache = new PresetFolderCache();

            return IsCacheForFolder(source.Cache, folder) ? source.Cache : null;
        }

        private void SavePresetFolderCache(FolderScanResult scan)
        {
            PresetFolderCache cache = GetPresetFolderCache(scan.Preset);
            if (cache == null)
                throw new InvalidOperationException(
                    "Could not save the Google Drive cache because its PresetFolder source is missing.");

            cache.FolderId = scan.Preset.Id;
            cache.HeaderPath = scan.Preset.Path ?? string.Empty;
            cache.RootName = scan.RootName;
            cache.FetchedFingerprint = scan.FetchedFingerprint;
            cache.ImportedFingerprint = scan.ImportedFingerprint;
            cache.LatestModifiedTime = scan.LatestModifiedTime == DateTimeOffset.MinValue
                ? string.Empty
                : scan.LatestModifiedTime.ToString("O", CultureInfo.InvariantCulture);
            cache.SkippedCount = scan.SkippedCount;
            if (cache.Files == null)
                cache.Files = new List<PresetFolderAssetCache>();

            cache.Files.Clear();
            cache.Files.AddRange(scan.Files);

            EditorUtility.SetDirty(presetFolderId);
            AssetDatabase.SaveAssetIfDirty(presetFolderId);
        }

        private void ResetCounters()
        {
            newFileCount = 0;
            changedFileCount = 0;
            existingFileCount = 0;
            skippedFileCount = 0;
        }

        private sealed class ImportFileWork
        {
            public FolderScanResult Scan;
            public PresetFolderAssetCache File;
            public string FileName;
            public string FilePath;
            public bool Existed;
            public float Progress;
        }

        private sealed class ImportProgressState
        {
            public double LastUpdateTime = -1d;
        }

        private sealed class FolderScanResult
        {
            public readonly FolderClone Preset;
            public readonly List<PresetFolderAssetCache> Files = new List<PresetFolderAssetCache>();
            public readonly List<string> FingerprintParts = new List<string>();
            public string RootName;
            public string FetchedFingerprint;
            public string ImportedFingerprint;
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
