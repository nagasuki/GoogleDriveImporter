#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace GoogleDriveImporter
{
    [Serializable]
    public class PresetFolder
    {
        [Tooltip("Google Drive folder ID to scan and import.")]
        public string Id;

#if ODIN_INSPECTOR
        [FolderPath]
#endif
        [Tooltip("Destination header path. The Drive folder's name is created below this path. For a child in a group, a relative path is added under the group path.")]
        public string Path;

        [HideInInspector]
        public PresetFolderCache Cache = new PresetFolderCache();
    }

    [Serializable]
    public sealed class PresetFolderGroup : PresetFolder
    {
        [Tooltip("Additional Drive folders imported under this group's path. Relative paths are added below the group path; Assets/... or an absolute path overrides it.")]
        public List<PresetFolder> Children = new List<PresetFolder>();
    }

    [Serializable]
    public sealed class PresetFolderCache
    {
        [HideInInspector]
        public string FolderId;

        [HideInInspector]
        public string HeaderPath;

        [HideInInspector]
        public string RootName;

        [HideInInspector]
        public string FetchedFingerprint;

        [HideInInspector]
        public string ImportedFingerprint;

        [HideInInspector]
        public string LatestModifiedTime;

        [HideInInspector]
        public int SkippedCount;

        [HideInInspector]
        public List<PresetFolderAssetCache> Files = new List<PresetFolderAssetCache>();
    }

    [Serializable]
    public sealed class PresetFolderAssetCache
    {
        [HideInInspector]
        public string Id;

        [HideInInspector]
        public string[] PathSegments;

        [HideInInspector]
        public long Size;

        [HideInInspector]
        public string Md5Checksum;

        [HideInInspector]
        public long LocalLastWriteTimeUtcTicks;

        [HideInInspector]
        public string LocalMd5Checksum;
    }

    [CreateAssetMenu(
        fileName = "PresetFolderId",
        menuName = "Google Drive Importer/Folder Preset")]
    public sealed class PresetFolderId : ScriptableObject
    {
        [Tooltip("Folder groups. A group's Drive ID is imported under its Path. Child folders can use their own IDs and destination paths.")]
        public List<PresetFolderGroup> Groups = new List<PresetFolderGroup>();

        [Tooltip("Legacy flat mappings from earlier versions. They remain supported; add new mappings under Groups.")]
        public List<PresetFolder> PresetFolders = new List<PresetFolder>();
    }
}
#endif
