#if UNITY_EDITOR
using UnityEngine;

namespace GoogleDriveImporter
{
    [CreateAssetMenu(
        fileName = "GoogleDriveEditorSettings",
        menuName = "Google Drive Importer/Editor Settings")]
    public sealed class GoogleDriveEditorSettings : ScriptableObject
    {
        public const int DefaultMaxParallelDownloads = 8;
        public const int MaxSupportedParallelDownloads = 32;

        [Tooltip("OAuth 2.0 Desktop application client ID from Google Cloud Console.")]
        public string ClientId;

        [Tooltip("Optional for Desktop OAuth clients. Google treats this client secret as non-secret.")]
        public string ClientSecret;

        [Tooltip("Read-only access is sufficient for this importer.")]
        public string Scope = "https://www.googleapis.com/auth/drive.readonly";

        [Range(1, MaxSupportedParallelDownloads)]
        [Tooltip("Maximum number of asset files downloaded at the same time. Higher values may trigger network or API throttling.")]
        public int MaxParallelDownloads = DefaultMaxParallelDownloads;

        [SerializeField, HideInInspector] private string accessToken;
        [SerializeField, HideInInspector] private string refreshToken;
        [SerializeField, HideInInspector] private long accessTokenExpiresAt;
        [SerializeField, HideInInspector] private string tokenClientId;
        [SerializeField, HideInInspector] private string tokenScope;
        [SerializeField, HideInInspector] private bool hasAuthorizationHistory;

        internal bool CanMigrateLegacyTokens { get { return !hasAuthorizationHistory; } }
        internal string AccessToken { get { return MatchesAuthorization ? accessToken : null; } }
        internal string RefreshToken { get { return MatchesAuthorization ? refreshToken : null; } }
        internal long AccessTokenExpiresAt { get { return MatchesAuthorization ? accessTokenExpiresAt : 0L; } }
        internal string EffectiveScope
        {
            get { return string.IsNullOrWhiteSpace(Scope) ? "https://www.googleapis.com/auth/drive.readonly" : Scope; }
        }

        private bool MatchesAuthorization
        {
            get { return tokenClientId == ClientId && tokenScope == EffectiveScope; }
        }

        internal void StoreTokens(string access, string refresh, long expiresAt)
        {
            accessToken = access;
            refreshToken = refresh;
            accessTokenExpiresAt = expiresAt;
            tokenClientId = ClientId;
            tokenScope = EffectiveScope;
            hasAuthorizationHistory = true;
            SaveAuthorization();
        }

        internal void ClearTokens()
        {
            accessToken = null;
            refreshToken = null;
            accessTokenExpiresAt = 0L;
            tokenClientId = null;
            tokenScope = null;
            hasAuthorizationHistory = true;
            SaveAuthorization();
        }

        private void SaveAuthorization()
        {
            UnityEditor.EditorUtility.SetDirty(this);
            if (UnityEditor.AssetDatabase.Contains(this))
                UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
        }
    }

}
#endif
