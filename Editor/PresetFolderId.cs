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
