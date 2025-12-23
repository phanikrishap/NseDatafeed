using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QANinjaAdapter.Services.Auth
{
    /// <summary>
    /// Automated Zerodha token generation using HTTP-based OAuth flow.
    /// Replicates Python ZerodhaTokenGenerator logic for .NET.
    /// No browser automation required - uses direct API calls with TOTP.
    /// </summary>
    public class ZerodhaTokenGenerator
    {
        // Zerodha API endpoints
        private const string LOGIN_URL = "https://kite.trade/connect/login";
        private const string API_LOGIN_URL = "https://kite.zerodha.com/api/login";
        private const string API_TWOFA_URL = "https://kite.zerodha.com/api/twofa";
        private const string TOKEN_URL = "https://api.kite.trade/session/token";

        // Credentials
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string _userId;
        private readonly string _password;
        private readonly string _totpSecret;
        private readonly string _redirectUrl;

        // HTTP client with cookie support
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;

        /// <summary>
        /// Event raised when token generation status changes
        /// </summary>
        public event EventHandler<TokenGenerationEventArgs> StatusChanged;

        /// <summary>
        /// Initialize token generator with credentials
        /// </summary>
        public ZerodhaTokenGenerator(string apiKey, string apiSecret, string userId,
            string password, string totpSecret, string redirectUrl = "http://127.0.0.1:8001/callback")
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _userId = userId ?? throw new ArgumentNullException(nameof(userId));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _totpSecret = totpSecret ?? throw new ArgumentNullException(nameof(totpSecret));
            _redirectUrl = redirectUrl;

            // IMPORTANT: Force TLS 1.2 for HTTPS connections
            // This is often needed in NinjaTrader's .NET environment
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

            // Setup HTTP client with cookie container
            _cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                AllowAutoRedirect = false, // We'll handle redirects manually
                UseCookies = true,
                // Additional settings for compatibility
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60) // Increased timeout
            };

            // Set default headers - DON'T set Accept-Encoding, let the handler manage decompression
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            // NOTE: Don't add Accept-Encoding header manually - AutomaticDecompression handles this
            // Adding it manually can cause double-compressed or undecompressed responses
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        }

        /// <summary>
        /// Synchronous wrapper that runs token generation on a dedicated thread
        /// Use this method when calling from NinjaTrader to avoid sync context deadlocks
        /// </summary>
        /// <returns>Token data containing access_token and user info</returns>
        public TokenData GenerateTokenSync()
        {
            TokenData result = null;
            Exception caughtException = null;

            // Create a completely new thread to avoid any synchronization context issues
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    // Run the async method synchronously on this new thread
                    result = GenerateTokenAsyncInternal().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caughtException = ex;
                }
            });

            thread.IsBackground = true;
            thread.Start();
            thread.Join(TimeSpan.FromMinutes(2)); // Wait up to 2 minutes

            if (caughtException != null)
                throw caughtException;

            if (result == null)
                throw new TimeoutException("Token generation timed out after 2 minutes");

            return result;
        }

        /// <summary>
        /// Execute complete token generation flow
        /// </summary>
        /// <returns>Token data containing access_token and user info</returns>
        public async Task<TokenData> GenerateTokenAsync()
        {
            // Wrap in Task.Run to escape any sync context
            return await Task.Run(async () => await GenerateTokenAsyncInternal().ConfigureAwait(false)).ConfigureAwait(false);
        }

        /// <summary>
        /// Internal async implementation
        /// </summary>
        private async Task<TokenData> GenerateTokenAsyncInternal()
        {
            try
            {
                // Step 1: Get login page and extract hidden fields
                RaiseStatus("Step 1: Fetching login page...");
                var hiddenFields = await Step1_GetLoginPageAsync().ConfigureAwait(false);

                // Step 2: Submit credentials
                RaiseStatus("Step 2: Submitting credentials...");
                var requestId = await Step2_PostCredentialsAsync(hiddenFields).ConfigureAwait(false);

                // Step 3: Generate and submit TOTP
                RaiseStatus("Step 3: Generating and submitting TOTP...");
                var redirectUrl = await Step3_SubmitTotpAsync(requestId).ConfigureAwait(false);

                // Step 4: Extract request_token from redirect URL
                RaiseStatus("Step 4: Extracting request token...");
                var requestToken = Step4_ExtractRequestToken(redirectUrl);

                // Step 5: Exchange for access_token
                RaiseStatus("Step 5: Exchanging for access token...");
                var tokenData = await Step5_ExchangeForAccessTokenAsync(requestToken).ConfigureAwait(false);

                RaiseStatus("Token generation successful!");
                return tokenData;
            }
            catch (Exception ex)
            {
                RaiseStatus($"Token generation failed: {ex.Message}", true);
                throw;
            }
        }

        /// <summary>
        /// Step 1: GET login page and extract hidden fields
        /// Follows redirects manually since AllowAutoRedirect is false
        /// </summary>
        private async Task<Dictionary<string, string>> Step1_GetLoginPageAsync()
        {
            var url = $"{LOGIN_URL}?api_key={_apiKey}&v=3";
            var maxRedirects = 10;
            var redirectCount = 0;

            RaiseStatus($"Step 1: Starting HTTP request to {url}");

            while (redirectCount < maxRedirects)
            {
                RaiseStatus($"Step 1: Attempt {redirectCount + 1}, URL: {url.Substring(0, Math.Min(50, url.Length))}...");

                HttpResponseMessage response;
                try
                {
                    // Use Task.Run to ensure we're not blocked by sync context
                    response = await Task.Run(async () =>
                    {
                        return await _httpClient.GetAsync(url).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                    RaiseStatus($"Step 1: Got response {(int)response.StatusCode} {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    RaiseStatus($"Step 1: HTTP request failed: {ex.Message}", true);
                    throw new Exception($"HTTP request failed in Step 1: {ex.Message}", ex);
                }

                // If redirect, follow it
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    var location = response.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(location))
                    {
                        throw new Exception("Redirect response but no Location header in Step 1");
                    }

                    // Handle relative URLs
                    if (!location.StartsWith("http"))
                    {
                        var uri = new Uri(new Uri(url), location);
                        location = uri.ToString();
                    }

                    RaiseStatus($"Step 1: Following redirect to {location.Substring(0, Math.Min(50, location.Length))}...");
                    url = location;
                    redirectCount++;
                    continue;
                }

                // Got actual response
                response.EnsureSuccessStatusCode();
                RaiseStatus("Step 1: Reading response content...");
                var html = await Task.Run(async () =>
                {
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);

                RaiseStatus($"Step 1: Got HTML response ({html.Length} chars), extracting hidden fields...");
                return ExtractHiddenFields(html);
            }

            throw new Exception($"Max redirects ({maxRedirects}) reached in Step 1");
        }

        /// <summary>
        /// Step 2: POST credentials to login API
        /// </summary>
        private async Task<string> Step2_PostCredentialsAsync(Dictionary<string, string> hiddenFields)
        {
            // Prepare login payload
            var payload = new Dictionary<string, string>
            {
                { "user_id", _userId },
                { "password", _password }
            };

            // Add hidden fields
            foreach (var field in hiddenFields)
            {
                payload[field.Key] = field.Value;
            }

            RaiseStatus($"Step 2: Posting to {API_LOGIN_URL} with user_id={_userId}");

            // Set headers for this request - IMPORTANT: Accept JSON and no compression to avoid decompression issues
            var request = new HttpRequestMessage(HttpMethod.Post, API_LOGIN_URL);
            request.Content = new FormUrlEncodedContent(payload);
            request.Headers.Add("X-Kite-Version", "3");
            request.Headers.Add("Referer", LOGIN_URL);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            // Explicitly request no compression for API calls to avoid decompression issues
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("identity"));

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            RaiseStatus($"Step 2: Response status {(int)response.StatusCode}, content length {responseContent.Length}");

            // Check if response is HTML (error page) instead of JSON
            if (responseContent.TrimStart().StartsWith("<") || responseContent.TrimStart().StartsWith("!"))
            {
                // Log first 500 chars for debugging
                var preview = responseContent.Length > 500 ? responseContent.Substring(0, 500) + "..." : responseContent;
                RaiseStatus($"Step 2 ERROR: Received HTML instead of JSON. Preview: {preview}", true);
                throw new Exception($"API returned HTML instead of JSON. Status: {response.StatusCode}. The login API may have changed or there's a session issue.");
            }

            JObject data;
            try
            {
                data = JObject.Parse(responseContent);
            }
            catch (JsonException ex)
            {
                var preview = responseContent.Length > 200 ? responseContent.Substring(0, 200) + "..." : responseContent;
                RaiseStatus($"Step 2 ERROR: JSON parse failed. Response preview: {preview}", true);
                throw new Exception($"Failed to parse JSON response: {ex.Message}. Response: {preview}");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var errorMsg = data["message"]?.ToString() ?? "Login failed";
                throw new Exception($"Login failed: {errorMsg}");
            }

            if (data["status"]?.ToString() != "success")
            {
                throw new Exception($"Login failed: {data["message"]?.ToString() ?? "Unknown error"}");
            }

            var requestId = data["data"]?["request_id"]?.ToString();
            if (string.IsNullOrEmpty(requestId))
            {
                throw new Exception("No request_id received from login API");
            }

            return requestId;
        }

        /// <summary>
        /// Step 3: Generate and submit TOTP for 2FA
        /// </summary>
        private async Task<string> Step3_SubmitTotpAsync(string requestId)
        {
            // Generate TOTP
            var totpCode = GenerateTotp();
            RaiseStatus($"Step 3: Generated TOTP: {totpCode}");

            // Prepare TOTP payload
            var payload = new Dictionary<string, string>
            {
                { "user_id", _userId },
                { "request_id", requestId },
                { "twofa_value", totpCode },
                { "twofa_type", "totp" },
                { "skip_session", "true" }
            };

            RaiseStatus($"Step 3: Posting TOTP to {API_TWOFA_URL}");

            // Create request with JSON Accept header and no compression
            var request = new HttpRequestMessage(HttpMethod.Post, API_TWOFA_URL);
            request.Content = new FormUrlEncodedContent(payload);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("identity"));
            request.Headers.Add("X-Kite-Version", "3");

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            RaiseStatus($"Step 3: Response status {(int)response.StatusCode}, content length {responseContent.Length}");

            // Check if response is HTML instead of JSON
            if (responseContent.TrimStart().StartsWith("<") || responseContent.TrimStart().StartsWith("!"))
            {
                var preview = responseContent.Length > 500 ? responseContent.Substring(0, 500) + "..." : responseContent;
                RaiseStatus($"Step 3 ERROR: Received HTML instead of JSON. Preview: {preview}", true);
                throw new Exception($"2FA API returned HTML instead of JSON. Status: {response.StatusCode}");
            }

            JObject data;
            try
            {
                data = JObject.Parse(responseContent);
            }
            catch (JsonException ex)
            {
                var preview = responseContent.Length > 200 ? responseContent.Substring(0, 200) + "..." : responseContent;
                RaiseStatus($"Step 3 ERROR: JSON parse failed. Response preview: {preview}", true);
                throw new Exception($"Failed to parse 2FA JSON response: {ex.Message}. Response: {preview}");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var errorMsg = data["message"]?.ToString() ?? "2FA failed";
                throw new Exception($"2FA failed: {errorMsg}");
            }

            if (data["status"]?.ToString() != "success")
            {
                throw new Exception($"2FA failed: {data["message"]?.ToString() ?? "Unknown error"}");
            }

            // TOTP verified - now complete OAuth flow to get request_token
            RaiseStatus("TOTP verification successful - completing OAuth flow...");

            // Build OAuth URL with redirect_uri
            var oauthUrl = $"https://kite.trade/connect/login?api_key={_apiKey}&v=3&redirect_uri={Uri.EscapeDataString(_redirectUrl)}";

            // Manually follow redirects to capture the request_token
            var currentUrl = oauthUrl;
            var maxRedirects = 10;
            var redirectCount = 0;

            while (redirectCount < maxRedirects)
            {
                // Check if current URL contains request_token
                if (currentUrl.Contains("request_token="))
                {
                    return currentUrl;
                }

                var redirectResponse = await _httpClient.GetAsync(currentUrl).ConfigureAwait(false);

                // Check for redirect
                if ((int)redirectResponse.StatusCode >= 300 && (int)redirectResponse.StatusCode < 400)
                {
                    var location = redirectResponse.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(location))
                    {
                        throw new Exception("Redirect response but no Location header");
                    }

                    // Handle relative URLs
                    if (!location.StartsWith("http"))
                    {
                        var uri = new Uri(new Uri(currentUrl), location);
                        location = uri.ToString();
                    }

                    currentUrl = location;

                    // Check if redirect URL contains token
                    if (currentUrl.Contains("request_token="))
                    {
                        return currentUrl;
                    }

                    redirectCount++;
                }
                else
                {
                    // No more redirects
                    if (redirectResponse.RequestMessage?.RequestUri?.ToString().Contains("request_token=") == true)
                    {
                        return redirectResponse.RequestMessage.RequestUri.ToString();
                    }
                    throw new Exception($"OAuth flow completed but no request_token found. Status: {redirectResponse.StatusCode}");
                }
            }

            throw new Exception($"Max redirects ({maxRedirects}) reached without finding request_token");
        }

        /// <summary>
        /// Step 4: Extract request_token from redirect URL
        /// </summary>
        private string Step4_ExtractRequestToken(string redirectUrl)
        {
            var uri = new Uri(redirectUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var requestToken = query["request_token"];

            if (string.IsNullOrEmpty(requestToken))
            {
                throw new Exception($"No request_token in redirect URL: {redirectUrl}");
            }

            return requestToken;
        }

        /// <summary>
        /// Step 5: Exchange request_token for access_token
        /// </summary>
        private async Task<TokenData> Step5_ExchangeForAccessTokenAsync(string requestToken)
        {
            // Generate checksum: SHA256(api_key + request_token + api_secret)
            var checksum = GenerateChecksum(requestToken);

            var payload = new Dictionary<string, string>
            {
                { "api_key", _apiKey },
                { "request_token", requestToken },
                { "checksum", checksum }
            };

            var response = await _httpClient.PostAsync(TOKEN_URL, new FormUrlEncodedContent(payload)).ConfigureAwait(false);
            var jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var data = JObject.Parse(jsonResponse);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var errorMsg = data["message"]?.ToString() ?? "Token exchange failed";
                throw new Exception($"Token exchange failed: {errorMsg}");
            }

            if (data["status"]?.ToString() != "success")
            {
                throw new Exception($"Token exchange failed: {data["message"]?.ToString() ?? "Unknown error"}");
            }

            var tokenData = data["data"];
            var accessToken = tokenData?["access_token"]?.ToString();

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new Exception("No access_token received from token API");
            }

            return new TokenData
            {
                AccessToken = accessToken,
                UserId = tokenData?["user_id"]?.ToString() ?? _userId,
                UserName = tokenData?["user_name"]?.ToString() ?? "",
                Email = tokenData?["email"]?.ToString() ?? "",
                Broker = tokenData?["broker"]?.ToString() ?? "ZERODHA",
                ApiKey = _apiKey,
                GeneratedAt = DateTime.Now
            };
        }

        /// <summary>
        /// Extract hidden form fields from HTML
        /// </summary>
        private Dictionary<string, string> ExtractHiddenFields(string html)
        {
            var fields = new Dictionary<string, string>();
            var pattern = @"<input[^>]+type=[""']hidden[""'][^>]*>";

            foreach (Match match in Regex.Matches(html, pattern))
            {
                var fieldHtml = match.Value;
                var nameMatch = Regex.Match(fieldHtml, @"name=[""']([^""']+)[""']");
                var valueMatch = Regex.Match(fieldHtml, @"value=[""']([^""']*)[""']");

                if (nameMatch.Success)
                {
                    var name = nameMatch.Groups[1].Value;
                    var value = valueMatch.Success ? valueMatch.Groups[1].Value : "";
                    fields[name] = value;
                }
            }

            return fields;
        }

        /// <summary>
        /// Generate TOTP code using the stored secret
        /// </summary>
        private string GenerateTotp()
        {
            return TotpGenerator.GenerateTotp(_totpSecret);
        }

        /// <summary>
        /// Generate checksum for token exchange
        /// SHA256(api_key + request_token + api_secret)
        /// </summary>
        private string GenerateChecksum(string requestToken)
        {
            var data = _apiKey + requestToken + _apiSecret;
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Raise status changed event
        /// </summary>
        private void RaiseStatus(string message, bool isError = false)
        {
            StatusChanged?.Invoke(this, new TokenGenerationEventArgs(message, isError));
        }

        /// <summary>
        /// Check if token has expired (expires at midnight IST)
        /// </summary>
        public static bool IsTokenExpired(DateTime? tokenTimestamp)
        {
            if (!tokenTimestamp.HasValue)
                return true;

            // Get current IST time
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var nowIst = TimeZoneInfo.ConvertTime(DateTime.Now, istZone);
            var tokenIst = TimeZoneInfo.ConvertTime(tokenTimestamp.Value, istZone);

            // Token expires at midnight IST (date change)
            return tokenIst.Date != nowIst.Date;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Token data returned from successful authentication
    /// </summary>
    public class TokenData
    {
        public string AccessToken { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string Email { get; set; }
        public string Broker { get; set; }
        public string ApiKey { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Event args for token generation status updates
    /// </summary>
    public class TokenGenerationEventArgs : EventArgs
    {
        public string Message { get; }
        public bool IsError { get; }
        public DateTime Timestamp { get; }

        public TokenGenerationEventArgs(string message, bool isError = false)
        {
            Message = message;
            IsError = isError;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// TOTP (Time-based One-Time Password) Generator
    /// RFC 6238 compliant implementation
    /// </summary>
    public static class TotpGenerator
    {
        private const int DIGITS = 6;
        private const int TIME_STEP = 30;

        /// <summary>
        /// Generate a TOTP code from a Base32 encoded secret
        /// </summary>
        public static string GenerateTotp(string base32Secret)
        {
            var key = Base32Decode(base32Secret);
            var counter = GetCurrentCounter();
            var hash = ComputeHmacSha1(key, GetCounterBytes(counter));
            var code = TruncateHash(hash);
            return code.ToString().PadLeft(DIGITS, '0');
        }

        private static long GetCurrentCounter()
        {
            var unixTime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return unixTime / TIME_STEP;
        }

        private static byte[] GetCounterBytes(long counter)
        {
            var bytes = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }

        private static byte[] ComputeHmacSha1(byte[] key, byte[] data)
        {
            using (var hmac = new System.Security.Cryptography.HMACSHA1(key))
            {
                return hmac.ComputeHash(data);
            }
        }

        private static int TruncateHash(byte[] hash)
        {
            var offset = hash[hash.Length - 1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);
            return binary % (int)Math.Pow(10, DIGITS);
        }

        private static byte[] Base32Decode(string base32)
        {
            // Remove padding and convert to uppercase
            base32 = base32.TrimEnd('=').ToUpperInvariant();

            var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var output = new List<byte>();
            var bits = 0;
            var value = 0;

            foreach (var c in base32)
            {
                var index = alphabet.IndexOf(c);
                if (index < 0)
                    continue; // Skip invalid characters

                value = (value << 5) | index;
                bits += 5;

                if (bits >= 8)
                {
                    bits -= 8;
                    output.Add((byte)(value >> bits));
                    value &= (1 << bits) - 1;
                }
            }

            return output.ToArray();
        }
    }
}
