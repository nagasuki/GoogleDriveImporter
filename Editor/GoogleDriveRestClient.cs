#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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

        public IEnumerator ListFilesInFolder(string folderId, Action<GoogleDriveFileList> onDone)
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

                GoogleDriveFileList page = null;
                yield return GetJson<GoogleDriveFileList>(url, result => page = result);
                if (page != null && page.Files != null)
                    files.AddRange(page.Files);
                pageToken = page == null ? null : page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            onDone?.Invoke(new GoogleDriveFileList { files = files.ToArray() });
        }

        public IEnumerator GetFile(string fileId, Action<GoogleDriveFile> onDone)
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("File ID is empty.", nameof(fileId));

            string url = FilesEndpoint
                + "/" + EscapeUrl(fileId)
                + "?fields=" + EscapeUrl("id,name,mimeType,modifiedTime")
                + "&supportsAllDrives=true";

            GoogleDriveFile file = null;
            yield return GetJson<GoogleDriveFile>(url, result => file = result);
            onDone?.Invoke(file);
        }

        public IEnumerator DownloadFileToPath(string fileId, string destinationPath, Action<float> onProgress = null)
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
                    string accessToken = null;
                    yield return GoogleDriveAuth.GetValidAccessToken(settings, token => accessToken = token);

                    string url = FilesEndpoint + "/" + EscapeUrl(fileId) + "?alt=media&supportsAllDrives=true";
                    using (var request = UnityWebRequest.Get(url))
                    {
                        request.downloadHandler = new DownloadHandlerFile(temporaryPath);
                        request.SetRequestHeader("Authorization", "Bearer " + accessToken);

                        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                        while (!operation.isDone)
                        {
                            onProgress?.Invoke(request.downloadProgress < 0f ? 0f : request.downloadProgress);
                            yield return null;
                        }

                        onProgress?.Invoke(1f);
                        if (request.result != UnityWebRequest.Result.Success)
                        {
                            if (request.responseCode == 401 && attempt == 0)
                            {
                                GoogleDriveAuth.InvalidateAccessToken();
                                continue;
                            }

                            throw CreateRequestException(request);
                        }
                    }

                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                    File.Move(temporaryPath, destinationPath);
                    yield break;
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

        private IEnumerator GetJson<T>(string url, Action<T> onDone)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string accessToken = null;
                yield return GoogleDriveAuth.GetValidAccessToken(settings, token => accessToken = token);

                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + accessToken);
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        if (request.responseCode == 401 && attempt == 0)
                        {
                            GoogleDriveAuth.InvalidateAccessToken();
                            continue;
                        }

                        throw CreateRequestException(request);
                    }

                    T result = JsonUtility.FromJson<T>(request.downloadHandler.text);
                    if (result == null)
                        throw new InvalidOperationException("Google Drive returned an empty JSON response.");

                    onDone?.Invoke(result);
                    yield break;
                }
            }

            throw new InvalidOperationException("Google Drive request failed after refreshing the access token.");
        }

        private static GoogleDriveRequestException CreateRequestException(UnityWebRequest request)
        {
            string responseBody = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
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
        private const string ClientSecretKey = "GoogleDriveOAuthClientSecret";
        private const string ScopeKey = "GoogleDriveOAuthScope";

        public static bool HasRefreshToken
        {
            get { return !string.IsNullOrEmpty(Read(RefreshTokenKey)); }
        }

        public static bool HasStoredSettings
        {
            get { return !string.IsNullOrEmpty(ReadEditorPreference(ClientIdKey)); }
        }

        public static void StoreSettings(GoogleDriveEditorSettings settings)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ClientId))
                return;

            UnityEditor.EditorPrefs.SetString(ClientIdKey, settings.ClientId);
            UnityEditor.EditorPrefs.SetString(ClientSecretKey, settings.ClientSecret ?? string.Empty);
            UnityEditor.EditorPrefs.SetString(ScopeKey, settings.Scope ?? string.Empty);
        }

        public static GoogleDriveEditorSettings CreateStoredSettings()
        {
            string clientId = ReadEditorPreference(ClientIdKey);
            if (string.IsNullOrWhiteSpace(clientId))
                return null;

            var storedSettings = ScriptableObject.CreateInstance<GoogleDriveEditorSettings>();
            storedSettings.ClientId = clientId;
            storedSettings.ClientSecret = ReadEditorPreference(ClientSecretKey);
            storedSettings.Scope = ReadEditorPreference(ScopeKey);
            if (string.IsNullOrWhiteSpace(storedSettings.Scope))
                storedSettings.Scope = "https://www.googleapis.com/auth/drive.readonly";
            storedSettings.hideFlags = HideFlags.HideAndDontSave;
            return storedSettings;
        }

        public static IEnumerator GetValidAccessToken(GoogleDriveEditorSettings settings, Action<string> onDone)
        {
            string accessToken = Read(AccessTokenKey);
            long expiresAt = ReadLong(ExpiryKey);
            if (!string.IsNullOrEmpty(accessToken) && expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
            {
                onDone?.Invoke(accessToken);
                yield break;
            }

            string refreshedToken = null;
            yield return RefreshAccessToken(settings, token => refreshedToken = token);
            onDone?.Invoke(refreshedToken);
        }

        public static IEnumerator Authorize(GoogleDriveEditorSettings settings, Action<string> onStatus)
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
                    + "&scope=" + EscapeUrl(string.IsNullOrWhiteSpace(settings.Scope)
                        ? "https://www.googleapis.com/auth/drive.readonly"
                        : settings.Scope)
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

                float timeout = 300f;
                while (!callback.IsCompleted && timeout > 0f)
                {
                    timeout -= 1f / 60f;
                    yield return null;
                }

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
                yield return ExchangeCode(settings, code, redirectUri, verifier);
            }
        }

        public static void ClearStoredTokens()
        {
            Delete(AccessTokenKey);
            Delete(RefreshTokenKey);
            Delete(ExpiryKey);
            Save();
        }

        public static void InvalidateAccessToken()
        {
            Delete(AccessTokenKey);
            Delete(ExpiryKey);
            Save();
        }

        private static IEnumerator RefreshAccessToken(GoogleDriveEditorSettings settings, Action<string> onDone)
        {
            ValidateSettings(settings);
            string refreshToken = Read(RefreshTokenKey);
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

            GoogleOAuthTokenResponse response = null;
            yield return SendTokenRequest(fields, result => response = result);
            StoreTokenResponse(response, true);
            onDone?.Invoke(response.access_token);
        }

        private static IEnumerator ExchangeCode(
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

            GoogleOAuthTokenResponse response = null;
            yield return SendTokenRequest(fields, result => response = result);
            StoreTokenResponse(response, false);
        }

        private static IEnumerator SendTokenRequest(Dictionary<string, string> fields, Action<GoogleOAuthTokenResponse> onDone)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(BuildFormBody(fields));
            using (var request = new UnityWebRequest(TokenEndpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                yield return request.SendWebRequest();

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
                onDone?.Invoke(response);
            }
        }

        private static void StoreTokenResponse(GoogleOAuthTokenResponse response, bool keepExistingRefreshToken)
        {
            Write(AccessTokenKey, response.access_token);
            if (!keepExistingRefreshToken && !string.IsNullOrEmpty(response.refresh_token))
                Write(RefreshTokenKey, response.refresh_token);
            Write(ExpiryKey, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(response.expires_in, 60)).ToString());
            Save();
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

        private static void Write(string key, string value)
        {
            UnityEditor.EditorPrefs.SetString(key, value ?? string.Empty);
        }

        private static void Delete(string key)
        {
            if (UnityEditor.EditorPrefs.HasKey(key))
                UnityEditor.EditorPrefs.DeleteKey(key);
            if (PlayerPrefs.HasKey(key))
                PlayerPrefs.DeleteKey(key);
        }

        private static void Save()
        {
            PlayerPrefs.Save();
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
