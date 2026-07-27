#if UNITY_EDITOR
using UnityEngine;

namespace GoogleDriveImporter
{
    [CreateAssetMenu(
        fileName = "GoogleDriveEditorSettings",
        menuName = "Google Drive Importer/Editor Settings")]
    public sealed class GoogleDriveEditorSettings : ScriptableObject
    {
        [Tooltip("OAuth 2.0 Desktop application client ID from Google Cloud Console.")]
        public string ClientId;

        [Tooltip("Optional for Desktop OAuth clients. Google treats this client secret as non-secret.")]
        public string ClientSecret;

        [Tooltip("Read-only access is sufficient for this importer.")]
        public string Scope = "https://www.googleapis.com/auth/drive.readonly";
    }

}
#endif
