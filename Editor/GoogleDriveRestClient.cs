#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GoogleDriveImporter
{
    [Serializable]
    public sealed class GoogleDriveFile
    {
        public string id;
        public string name;
        public string mimeType;
        public string size;
        public string createdTime;
        public string modifiedTime;
        public string md5Checksum;

        public string Id { get { return id; } }
        public string Name { get { return name; } }
        public string MimeType { get { return mimeType; } }
        public long Size
        {
            get
            {
                long value;
                return long.TryParse(size, out value) ? value : 0L;
            }
        }
        public string CreatedTime { get { return createdTime; } }
        public string ModifiedTime { get { return modifiedTime; } }
        public string Md5Checksum { get { return md5Checksum; } }

        public bool IsFolder
        {
            get { return MimeType == "application/vnd.google-apps.folder"; }
        }

        public bool IsBinaryFile
        {
            get { return !string.IsNullOrEmpty(MimeType) && !MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal); }
        }
    }

    [Serializable]
    public sealed class GoogleDriveFileList
    {
        public GoogleDriveFile[] files;
        public string nextPageToken;

        public GoogleDriveFile[] Files { get { return files; } }
        public string NextPageToken { get { return nextPageToken; } }
    }

    internal sealed class GoogleDriveRestClient : IDisposable
    {
        private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
        private readonly GoogleDriveEditorSettings settings;

        public GoogleDriveRestClient(GoogleDriveEditorSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.ClientId))
                throw new InvalidOperationException("GoogleDriveEditorSettings.ClientId is empty.");
        }

        public async Task<GoogleDriveFileList> ListFilesInFolder(string folderId)
        {
            if (string.IsNullOrWhiteSpace(folderId))
                throw new ArgumentException("Folder ID is empty.", nameof(folderId));

            var files = new List<GoogleDriveFile>();
            string pageToken = null;

            do
            {
                string query = "'" + folderId.Replace("'", "\\'") + "' in parents and trashed = false";
                string url = FilesEndpoint
                    + "?q=" + EscapeUrl(query)
                    + "&pageSize=1000"
                    + "&fields=" + EscapeUrl("nextPageToken,files(id,name,mimeType,size,createdTime,modifiedTime,md5Checksum)")
                    + "&spaces=drive"
                    + "&corpora=allDrives"
                    + "&includeItemsFromAllDrives=true"
                    + "&supportsAllDrives=true";

                if (!string.IsNullOrEmpty(pageToken))
                    url += "&pageToken=" + EscapeUrl(pageToken);

                GoogleDriveFileList page = await GetJson<GoogleDriveFileList>(url);
                if (page != null && page.Files != null)
                    files.AddRange(page.Files);
                pageToken = page == null ? null : page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return new GoogleDriveFileList { files = files.ToArray() };
        }

        public Task<GoogleDriveFile> GetFile(string fileId)
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("File ID is empty.", nameof(fileId));

            string url = FilesEndpoint
                + "/" + EscapeUrl(fileId)
                + "?fields=" + EscapeUrl("id,name,mimeType,modifiedTime")
                + "&supportsAllDrives=true";

            return GetJson<GoogleDriveFile>(url);
        }

        public async Task DownloadFileToPath(string fileId, string destinationPath, Action<float> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("File ID is empty.", nameof(fileId));

            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = destinationPath
                + ".gdrive-download-"
                + Guid.NewGuid().ToString("N");
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    string accessToken = await GoogleDriveAuth.GetValidAccessToken(settings);

                    string url = FilesEndpoint + "/" + EscapeUrl(fileId) + "?alt=media&supportsAllDrives=true";
                    using (var request = UnityWebRequest.Get(url))
                    {
                        request.downloadHandler = new DownloadHandlerFile(temporaryPath);
                        request.SetRequestHeader("Authorization", "Bearer " + accessToken);

                        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                        await EditorAsyncRunner.WaitForOperation(operation, () =>
                            onProgress?.Invoke(request.downloadProgress < 0f ? 0f : request.downloadProgress));

                        onProgress?.Invoke(1f);
                        if (request.result != UnityWebRequest.Result.Success)
                        {
                            if (request.responseCode == 401 && attempt == 0)
                            {
                                GoogleDriveAuth.InvalidateAccessToken(settings, accessToken);
                                continue;
                            }

                            throw CreateRequestException(request);
                        }
                    }

                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                    File.Move(temporaryPath, destinationPath);
                    return;
                }

                throw new InvalidOperationException("Google Drive download failed after refreshing the access token.");
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public void Dispose()
        {
        }

        private async Task<T> GetJson<T>(string url)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string accessToken = await GoogleDriveAuth.GetValidAccessToken(settings);

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + accessToken);
                    await EditorAsyncRunner.WaitForOperation(request.SendWebRequest());

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        if (request.responseCode == 401 && attempt == 0)
                        {
                            GoogleDriveAuth.InvalidateAccessToken(settings, accessToken);
                            continue;
                        }

                        throw CreateRequestException(request);
                    }

                    T result = JsonUtility.FromJson<T>(request.downloadHandler.text);
                    if (result == null)
                        throw new InvalidOperationException("Google Drive returned an empty JSON response.");

                    return result;
                }
            }

            throw new InvalidOperationException("Google Drive request failed after refreshing the access token.");
        }

        private static GoogleDriveRequestException CreateRequestException(UnityWebRequest request)
        {
            string responseBody = request.downloadHandler is DownloadHandlerBuffer
                ? request.downloadHandler.text
                : string.Empty;
            return new GoogleDriveRequestException(request.responseCode, request.error, responseBody);
        }

        private static string EscapeUrl(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }
    }

    internal static class GoogleDriveAuth
    {
        private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string AccessTokenKey = "GoogleDriveAccessToken";
        private const string RefreshTokenKey = "GoogleDriveRefreshToken";
        private const string ExpiryKey = "GoogleDriveAccessTokenExpiresAt";
        private const string ClientIdKey = "GoogleDriveOAuthClientId";
        private const string ScopeKey = "GoogleDriveOAuthScope";
        private static readonly HashSet<GoogleDriveEditorSettings> Refreshing = new HashSet<GoogleDriveEditorSettings>();

        [UnityEditor.InitializeOnLoadMethod]
        private static void MigrateProjectAuthorization()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                string[] settingsGuids = UnityEditor.AssetDatabase.FindAssets("t:GoogleDriveEditorSettings");
                foreach (string guid in settingsGuids)
                {
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GoogleDriveEditorSettings>(
                        UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                    MigrateStoredTokens(asset);
                }
            };
        }

        public static bool HasRefreshToken(GoogleDriveEditorSettings settings)
        {
            return settings != null && !string.IsNullOrEmpty(settings.RefreshToken);
        }

        public static void MigrateStoredTokens(GoogleDriveEditorSettings settings)
        {
            if (settings == null || !settings.CanMigrateLegacyTokens || HasRefreshToken(settings)
                || !string.Equals(settings.ClientId, ReadEditorPreference(ClientIdKey), StringComparison.Ordinal))
                return;

            string storedScope = ReadEditorPreference(ScopeKey);
            if (string.IsNullOrWhiteSpace(storedScope))
                storedScope = "https://www.googleapis.com/auth/drive.readonly";
            string refreshToken = Read(RefreshTokenKey);
            if (settings.EffectiveScope != storedScope || string.IsNullOrEmpty(refreshToken))
                return;

            settings.StoreTokens(Read(AccessTokenKey), refreshToken, ReadLong(ExpiryKey));
        }

        public static async Task<string> GetValidAccessToken(GoogleDriveEditorSettings settings)
        {
            ValidateSettings(settings);
            MigrateStoredTokens(settings);
            if (Refreshing.Contains(settings))
                await EditorAsyncRunner.WaitUntil(() => !Refreshing.Contains(settings));

            string accessToken = settings.AccessToken;
            long expiresAt = settings.AccessTokenExpiresAt;
            if (!string.IsNullOrEmpty(accessToken) && expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
                return accessToken;

            Refreshing.Add(settings);
            try
            {
                return await RefreshAccessToken(settings);
            }
            finally
            {
                Refreshing.Remove(settings);
            }
        }

        public static async Task Authorize(GoogleDriveEditorSettings settings, Action<string> onStatus)
        {
            ValidateSettings(settings);
            onStatus?.Invoke("Starting local OAuth callback listener...");

            int port = GetFreePort();
            string redirectUri = "http://127.0.0.1:" + port + "/oauth2callback";
            string state = CreateRandomUrlSafeString(24);
            string verifier = CreateRandomUrlSafeString(48);
            string challenge = CreateCodeChallenge(verifier);

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();

                string authorizationUrl = AuthorizationEndpoint
                    + "?client_id=" + EscapeUrl(settings.ClientId)
                    + "&redirect_uri=" + EscapeUrl(redirectUri)
                    + "&response_type=code"
                    + "&scope=" + EscapeUrl(settings.EffectiveScope)
                    + "&access_type=offline"
                    + "&prompt=consent"
                    + "&code_challenge=" + EscapeUrl(challenge)
                    + "&code_challenge_method=S256"
                    + "&state=" + EscapeUrl(state);

                var callback = new OAuthCallbackState();
                listener.BeginGetContext(
                    result => callback.Complete(listener, result),
                    null);

                Application.OpenURL(authorizationUrl);
                onStatus?.Invoke("Complete authorization in the browser...");

                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(5);
                await EditorAsyncRunner.WaitUntil(() => callback.IsCompleted || DateTimeOffset.UtcNow >= deadline);

                if (!callback.IsCompleted)
                    throw new TimeoutException("Timed out waiting for the Google OAuth callback.");
                if (callback.Error != null)
                    throw callback.Error;

                HttpListenerContext context = callback.Context;
                string callbackState = context.Request.QueryString["state"];
                string code = context.Request.QueryString["code"];
                string error = context.Request.QueryString["error"];
                WriteCallbackResponse(context, string.IsNullOrEmpty(error)
                    ? "Authorization completed. You can return to Unity."
                    : "Authorization failed. You can return to Unity.");

                if (!string.IsNullOrEmpty(error))
                    throw new InvalidOperationException("Google OAuth failed: " + error);
                if (!string.Equals(state, callbackState, StringComparison.Ordinal))
                    throw new InvalidOperationException("Google OAuth state validation failed.");
                if (string.IsNullOrEmpty(code))
                    throw new InvalidOperationException("Google OAuth did not return an authorization code.");

                onStatus?.Invoke("Exchanging authorization code for tokens...");
                await ExchangeCode(settings, code, redirectUri, verifier);
            }
        }

        public static void ClearStoredTokens(GoogleDriveEditorSettings settings)
        {
            if (settings == null)
                return;
            settings.ClearTokens();
        }

        public static void InvalidateAccessToken(GoogleDriveEditorSettings settings, string rejectedToken = null)
        {
            // Another request may already have refreshed the token after this
            // request was sent. An old 401 must not invalidate that newer token.
            if (rejectedToken != null && settings.AccessToken != rejectedToken)
                return;
            settings.StoreTokens(null, settings.RefreshToken, 0L);
        }

        private static async Task<string> RefreshAccessToken(GoogleDriveEditorSettings settings)
        {
            ValidateSettings(settings);
            string refreshToken = settings.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
                throw new InvalidOperationException("No Google refresh token is stored. Authorize Google Drive first.");

            var fields = new Dictionary<string, string>
            {
                { "client_id", settings.ClientId },
                { "refresh_token", refreshToken },
                { "grant_type", "refresh_token" }
            };
            if (!string.IsNullOrWhiteSpace(settings.ClientSecret))
                fields.Add("client_secret", settings.ClientSecret);

            GoogleOAuthTokenResponse response = await SendTokenRequest(fields);
            StoreTokenResponse(settings, response, true);
            return response.access_token;
        }

        private static async Task ExchangeCode(
            GoogleDriveEditorSettings settings,
            string code,
            string redirectUri,
            string verifier)
        {
            var fields = new Dictionary<string, string>
            {
                { "client_id", settings.ClientId },
                { "code", code },
                { "code_verifier", verifier },
                { "redirect_uri", redirectUri },
                { "grant_type", "authorization_code" }
            };
            if (!string.IsNullOrWhiteSpace(settings.ClientSecret))
                fields.Add("client_secret", settings.ClientSecret);

            GoogleOAuthTokenResponse response = await SendTokenRequest(fields);
            StoreTokenResponse(settings, response, false);
        }

        private static async Task<GoogleOAuthTokenResponse> SendTokenRequest(Dictionary<string, string> fields)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(BuildFormBody(fields));
            using (var request = new UnityWebRequest(TokenEndpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                await EditorAsyncRunner.WaitForOperation(request.SendWebRequest());

                if (request.result != UnityWebRequest.Result.Success)
                {
                    GoogleOAuthTokenResponse error = TryParseTokenResponse(request.downloadHandler.text);
                    string description = error == null || string.IsNullOrEmpty(error.error_description)
                        ? request.error
                        : error.error_description;
                    throw new InvalidOperationException("Google OAuth token request failed: " + description);
                }

                GoogleOAuthTokenResponse response = TryParseTokenResponse(request.downloadHandler.text);
                if (response == null || string.IsNullOrEmpty(response.access_token))
                    throw new InvalidOperationException("Google OAuth returned an invalid token response.");
                return response;
            }
        }

        private static void StoreTokenResponse(GoogleDriveEditorSettings settings, GoogleOAuthTokenResponse response, bool keepExistingRefreshToken)
        {
            string refreshToken = !string.IsNullOrEmpty(response.refresh_token)
                ? response.refresh_token
                : keepExistingRefreshToken ? settings.RefreshToken : null;
            settings.StoreTokens(response.access_token, refreshToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(response.expires_in, 0));
        }

        private static GoogleOAuthTokenResponse TryParseTokenResponse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                return JsonUtility.FromJson<GoogleOAuthTokenResponse>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void ValidateSettings(GoogleDriveEditorSettings value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ClientId))
                throw new InvalidOperationException("Assign GoogleDriveEditorSettings with a Client ID first.");
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string CreateRandomUrlSafeString(int byteCount)
        {
            byte[] bytes = new byte[byteCount];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string CreateCodeChallenge(string verifier)
        {
            using (var sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string BuildFormBody(Dictionary<string, string> fields)
        {
            var parts = new List<string>();
            foreach (var field in fields)
                parts.Add(EscapeUrl(field.Key) + "=" + EscapeUrl(field.Value));
            return string.Join("&", parts);
        }

        private static string EscapeUrl(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static void WriteCallbackResponse(HttpListenerContext context, string message)
        {
            byte[] content = Encoding.UTF8.GetBytes(
                "<html><body><h3>" + message + "</h3><p>You may close this tab.</p></body></html>");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = content.Length;
            context.Response.OutputStream.Write(content, 0, content.Length);
            context.Response.Close();
        }

        private static string Read(string key)
        {
            if (UnityEditor.EditorPrefs.HasKey(key))
                return UnityEditor.EditorPrefs.GetString(key);
            return PlayerPrefs.GetString(key, string.Empty);
        }

        private static string ReadEditorPreference(string key)
        {
            return UnityEditor.EditorPrefs.HasKey(key)
                ? UnityEditor.EditorPrefs.GetString(key)
                : string.Empty;
        }

        private static long ReadLong(string key)
        {
            long value;
            return long.TryParse(Read(key), out value) ? value : 0L;
        }

        private sealed class OAuthCallbackState
        {
            private readonly object syncRoot = new object();
            private bool completed;
            private HttpListenerContext context;
            private Exception error;

            public bool IsCompleted
            {
                get { lock (syncRoot) return completed; }
            }

            public HttpListenerContext Context
            {
                get { lock (syncRoot) return context; }
            }

            public Exception Error
            {
                get { lock (syncRoot) return error; }
            }

            public void Complete(HttpListener listener, IAsyncResult result)
            {
                try
                {
                    HttpListenerContext callbackContext = listener.EndGetContext(result);
                    lock (syncRoot)
                    {
                        context = callbackContext;
                        completed = true;
                    }
                }
                catch (Exception exception)
                {
                    lock (syncRoot)
                    {
                        error = exception;
                        completed = true;
                    }
                }
            }
        }
    }

    [Serializable]
    internal sealed class GoogleOAuthTokenResponse
    {
        public string access_token;
        public int expires_in;
        public string refresh_token;
        public string token_type;
        public string error;
        public string error_description;
    }

    internal sealed class GoogleDriveRequestException : Exception
    {
        public long StatusCode { get; private set; }
        public string ResponseBody { get; private set; }

        public GoogleDriveRequestException(long statusCode, string error, string responseBody)
            : base(string.Format("Google Drive request failed ({0}): {1}", statusCode, string.IsNullOrEmpty(error) ? "Unknown error" : error))
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
#endif
