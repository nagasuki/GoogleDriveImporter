#if UNITY_EDITOR
using UnityEngine;

namespace GoogleDriveImporter
{
    [CreateAssetMenu(
        fileName = "GoogleDriveEditorSettings",
        menuName = "Google Drive Importer/Editor Settings")]
    public sealed class GoogleDriveEditorSettings : ScriptableObject
    {
        public const int DefaultMaxParallelDownloads = 4;
        public const int MaxSupportedParallelDownloads = 8;

        [Tooltip("OAuth 2.0 Desktop application client ID from Google Cloud Console.")]
        public string ClientId;

        [Tooltip("Optional for Desktop OAuth clients. Google treats this client secret as non-secret.")]
        public string ClientSecret;

        [Tooltip("Read-only access is sufficient for this importer.")]
        public string Scope = "https://www.googleapis.com/auth/drive.readonly";

        [Range(1, MaxSupportedParallelDownloads)]
        [Tooltip("Maximum number of asset files downloaded at the same time. Higher values may trigger network or API throttling.")]
        public int MaxParallelDownloads = DefaultMaxParallelDownloads;
    }

}
#endif
