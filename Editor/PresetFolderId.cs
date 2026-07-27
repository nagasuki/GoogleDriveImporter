#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoogleDriveImporter
{
    [Serializable]
    public sealed class PresetFolder
    {
        public string Id;
        public string Path;

        [HideInInspector]
        public PresetFolderCache Cache = new PresetFolderCache();
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
    }

    [CreateAssetMenu(
        fileName = "PresetFolderId",
        menuName = "Nanuq Tools/Google Drive/Folder Preset")]
    public sealed class PresetFolderId : ScriptableObject
    {
        public List<PresetFolder> PresetFolders = new List<PresetFolder>();
    }
}
#endif
