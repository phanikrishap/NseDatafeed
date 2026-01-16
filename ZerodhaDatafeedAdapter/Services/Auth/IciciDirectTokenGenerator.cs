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

using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.Auth
{
    /// <summary>
    /// Automated ICICI Direct Breeze token generation using HTTP-based flow.
    /// NOTE: ICICI's login flow is more restrictive than Zerodha's:
    /// - Zerodha exposes /api/login and /api/twofa JSON endpoints
    /// - ICICI uses traditional form-based login with server-side rendering
    /// - This class attempts HTTP-based login but may require Selenium in some cases
    /// </summary>
    public class IciciDirectTokenGenerator : IDisposable
    {
        // ICICI login endpoints
        private const string LOGIN_PAGE_URL = "https://api.icicidirect.com/apiuser/login";
        private const string TRADE_LOGIN_URL = "https://api.icicidirect.com/apiuser/tradelogin";
        private const string CUSTOMER_DETAILS_URL = "https://api.icicidirect.com/breezeapi/api/v1/customerdetails";

        // Credentials
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string _userId;
        private readonly string _password;
        private readonly string _totpSecret;

        // HTTP client with cookie support
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;

        // Track last redirect URL
        private string _lastRedirectUrl;

        /// <summary>
        /// Event raised when token generation status changes
        /// </summary>
        public event EventHandler<IciciTokenEventArgs> StatusChanged;

        public IciciDirectTokenGenerator(string apiKey, string apiSecret, string userId,
            string password, string totpSecret)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _userId = userId ?? throw new ArgumentNullException(nameof(userId));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _totpSecret = totpSecret ?? throw new ArgumentNullException(nameof(totpSecret));

            // Force TLS 1.2
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            // Setup HTTP client with cookie container
            _cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                AllowAutoRedirect = false,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            // Set browser-like headers
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        }

        /// <summary>
        /// Synchronous wrapper for token generation (for use in NinjaTrader context)
        /// </summary>
        public IciciTokenResult GenerateTokenSync()
        {
            IciciTokenResult result = null;
            Exception caughtException = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    result = GenerateTokenAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caughtException = ex;
                }
            });

            thread.IsBackground = true;
            thread.Start();
            thread.Join(TimeSpan.FromMinutes(2));

            if (caughtException != null)
            {
                return new IciciTokenResult
                {
                    Success = false,
                    Error = caughtException.Message
                };
            }

            return result ?? new IciciTokenResult
            {
                Success = false,
                Error = "Token generation timed out after 2 minutes"
            };
        }

        /// <summary>
        /// Attempt to generate session token using HTTP-based flow
        /// </summary>
        public async Task<IciciTokenResult> GenerateTokenAsync()
        {
            try
            {
                RaiseStatus("Step 1: Fetching login page...");

                // Step 1: Get initial redirect page (contains form that posts to /tradelogin)
                var loginUrl = $"{LOGIN_PAGE_URL}?api_key={Uri.EscapeDataString(_apiKey)}";
                var initialResponse = await GetWithRedirectsAsync(loginUrl);

                if (initialResponse.Contains("Public Key does not exist"))
                {
                    return new IciciTokenResult
                    {
                        Success = false,
                        Error = "Invalid API Key - Public Key does not exist"
                    };
                }

                RaiseStatus($"Step 1: Got initial page ({initialResponse.Length} chars)");

                // Step 2: Extract form action and data from initial page
                RaiseStatus("Step 2: Extracting form details...");
                var initialFormData = ExtractFormData(initialResponse);
                var formAction = ExtractFormAction(initialResponse);
                RaiseStatus($"Step 2: Form action: {formAction ?? "none"}, Fields: {initialFormData.Count}");

                // Step 3: POST to /tradelogin to get the actual login form
                RaiseStatus("Step 3: Submitting to tradelogin to get login form...");
                var postUrl = TRADE_LOGIN_URL;
                if (!string.IsNullOrEmpty(formAction) && !formAction.StartsWith("http"))
                {
                    postUrl = "https://api.icicidirect.com/apiuser/" + formAction;
                }

                var loginPageResponse = await PostFormAsync(postUrl, initialFormData, loginUrl);
                RaiseStatus($"Step 3: Got login form page ({loginPageResponse.Length} chars)");

                // Check if this is the actual login page (contains login form fields)
                bool isLoginPage = loginPageResponse.Contains("User/Login Id") ||
                                   loginPageResponse.Contains("txtuid") ||
                                   loginPageResponse.Contains("Password");

                Dictionary<string, string> submitFormData = null;

                if (isLoginPage)
                {
                    RaiseStatus("Step 3.5: Login form detected - extracting fields and submitting credentials...");

                    // Extract encryption key from page
                    var encKeyMatch = Regex.Match(loginPageResponse, @"id=[""']hidenc[""'][^>]*value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    if (!encKeyMatch.Success)
                    {
                        encKeyMatch = Regex.Match(loginPageResponse, @"value=[""']([^""']*)[""'][^>]*id=[""']hidenc[""']", RegexOptions.IgnoreCase);
                    }
                    var encKey = encKeyMatch.Success ? encKeyMatch.Groups[1].Value : "";

                    // Encrypt password if encryption key is available
                    string encryptedPassword = _password;
                    if (!string.IsNullOrEmpty(encKey) && encKey.Contains("~"))
                    {
                        try
                        {
                            encryptedPassword = EncryptPasswordWithRsa(_password, encKey);
                            RaiseStatus($"Step 3.5: Password encrypted (length: {encryptedPassword.Length})");
                        }
                        catch (Exception ex)
                        {
                            RaiseStatus($"Step 3.5: RSA encryption failed: {ex.Message}");
                        }
                    }

                    // Build form data - extract ALL inputs by ID (ICICI JS uses IDs, not names)
                    submitFormData = ExtractAllInputsById(loginPageResponse);
                    submitFormData["txtuid"] = _userId;
                    submitFormData["txtPass"] = "************";  // JS masks password
                    submitFormData["hidp"] = encryptedPassword;  // Encrypted password goes here
                    submitFormData["chkssTnc"] = "Y";
                    submitFormData["hiddob"] = "";
                    submitFormData["btnSubmit"] = "Login";

                    RaiseStatus($"Step 3.5: Submitting credentials with {submitFormData.Count} fields");

                    // POST to /getotp endpoint
                    var getOtpUrl = "https://api.icicidirect.com/apiuser/tradelogin/getotp";
                    RaiseStatus($"Step 3.5: Posting to {getOtpUrl}...");

                    loginPageResponse = await PostFormAsync(getOtpUrl, submitFormData, TRADE_LOGIN_URL);
                    RaiseStatus($"Step 3.5: OTP response ({loginPageResponse.Length} chars)");
                }

                // Check if we need TOTP
                if (loginPageResponse.Contains("pnlOTP") || loginPageResponse.Contains("hiotp") || loginPageResponse.Contains("Verify TOTP"))
                {
                    RaiseStatus("Step 4: TOTP page detected, generating TOTP...");

                    var totp = TotpGenerator.GenerateTotp(_totpSecret);
                    RaiseStatus($"Step 4: Generated TOTP: {totp}");

                    // Extract fields from OTP page by ID
                    var otpFormData = ExtractAllInputsById(loginPageResponse);
                    otpFormData["hiotp"] = totp;

                    // Merge with original form data (preserve session fields)
                    if (submitFormData != null)
                    {
                        foreach (var kvp in submitFormData)
                        {
                            if (!otpFormData.ContainsKey(kvp.Key))
                                otpFormData[kvp.Key] = kvp.Value;
                        }
                    }

                    RaiseStatus($"Step 4: Submitting TOTP with {otpFormData.Count} fields");

                    // Submit TOTP
                    var validateUrl = "https://api.icicidirect.com/apiuser/tradelogin/validateuser";
                    RaiseStatus($"Step 4: Posting to {validateUrl}...");

                    var totpResponse = await PostFormAsync(validateUrl, otpFormData, TRADE_LOGIN_URL);
                    RaiseStatus($"Step 4: TOTP response ({totpResponse.Length} chars)");

                    // Check for apisession in response
                    if (totpResponse.Contains("apisession="))
                    {
                        var sessionToken = ExtractSessionToken(totpResponse);
                        RaiseStatus($"Step 4: Found session token in TOTP response!");
                        return new IciciTokenResult
                        {
                            Success = true,
                            SessionToken = sessionToken,
                            GeneratedAt = DateTime.Now
                        };
                    }

                    // Check redirect URL
                    if (_lastRedirectUrl != null && _lastRedirectUrl.Contains("apisession="))
                    {
                        var sessionToken = ExtractSessionToken(_lastRedirectUrl);
                        RaiseStatus($"Step 4: Found session token in redirect URL!");
                        return new IciciTokenResult
                        {
                            Success = true,
                            SessionToken = sessionToken,
                            GeneratedAt = DateTime.Now
                        };
                    }
                }

                // Check final URL for apisession
                if (!string.IsNullOrEmpty(_lastRedirectUrl) && _lastRedirectUrl.Contains("apisession="))
                {
                    var sessionToken = ExtractSessionToken(_lastRedirectUrl);
                    return new IciciTokenResult
                    {
                        Success = true,
                        SessionToken = sessionToken,
                        GeneratedAt = DateTime.Now
                    };
                }

                return new IciciTokenResult
                {
                    Success = false,
                    Error = "Could not complete login flow - apisession not found. ICICI may require Selenium-based automation."
                };
            }
            catch (Exception ex)
            {
                RaiseStatus($"Error: {ex.Message}", true);
                return new IciciTokenResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Validate an existing session token by calling customer details API
        /// </summary>
        public async Task<bool> ValidateSessionAsync(string sessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey))
                return false;

            try
            {
                RaiseStatus("Validating ICICI session token...");

                var requestBody = new Dictionary<string, string>
                {
                    { "SessionToken", sessionKey },
                    { "AppKey", _apiKey }
                };
                var bodyJson = JsonConvert.SerializeObject(requestBody);

                // Use WebRequest with reflection hack for GET with body (ICICI API requirement)
                var request = WebRequest.Create(new Uri(CUSTOMER_DETAILS_URL));
                request.ContentType = "application/json";
                request.Method = "GET";

                // Reflection hack to allow body in GET request (.NET Framework specific)
                var type = request.GetType();
                var currentMethod = type.GetProperty("CurrentMethod",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(request);
                var methodType = currentMethod.GetType();
                methodType.GetField("ContentBodyNotAllowed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(currentMethod, false);

                // Write body
                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(bodyJson);
                }

                // Get response
                var response = await Task.Run(() => request.GetResponse());
                string responseContent;
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    responseContent = reader.ReadToEnd();
                }
                response.Close();

                var jsonResponse = JObject.Parse(responseContent);

                if (jsonResponse["Status"]?.Value<int>() == 200)
                {
                    RaiseStatus("ICICI session token is valid");
                    return true;
                }

                RaiseStatus($"ICICI session token invalid: {jsonResponse["Error"]?.ToString() ?? "Unknown error"}");
                return false;
            }
            catch (Exception ex)
            {
                RaiseStatus($"ICICI session validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if session token needs refresh (generated more than 12 hours ago)
        /// </summary>
        public static bool IsSessionExpired(DateTime? sessionGeneratedAt)
        {
            if (!sessionGeneratedAt.HasValue)
                return true;

            // ICICI sessions typically last 24 hours, but we refresh at 12 hours to be safe
            return (DateTime.Now - sessionGeneratedAt.Value).TotalHours > 12;
        }

        #region Private Methods

        private async Task<string> GetWithRedirectsAsync(string url, int maxRedirects = 10)
        {
            if (!url.StartsWith("http"))
            {
                url = "https://api.icicidirect.com" + (url.StartsWith("/") ? "" : "/") + url;
            }

            var currentUrl = url;
            var redirectCount = 0;

            while (redirectCount < maxRedirects)
            {
                var response = await _httpClient.GetAsync(currentUrl);
                _lastRedirectUrl = currentUrl;

                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    var location = response.Headers.Location?.ToString();
                    if (string.IsNullOrEmpty(location))
                        break;

                    if (!location.StartsWith("http"))
                    {
                        var uri = new Uri(new Uri(currentUrl), location);
                        location = uri.ToString();
                    }

                    currentUrl = location;
                    _lastRedirectUrl = location;

                    if (location.Contains("apisession="))
                    {
                        return location;
                    }

                    redirectCount++;
                    continue;
                }

                return await response.Content.ReadAsStringAsync();
            }

            return "";
        }

        private async Task<string> PostFormAsync(string url, Dictionary<string, string> formData, string referer)
        {
            if (!url.StartsWith("http"))
            {
                url = "https://api.icicidirect.com" + (url.StartsWith("/") ? "" : "/") + url;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new FormUrlEncodedContent(formData);
            request.Headers.Add("Referer", referer);

            var response = await _httpClient.SendAsync(request);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                var location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    if (!location.StartsWith("http"))
                    {
                        var uri = new Uri(new Uri(url), location);
                        location = uri.ToString();
                    }
                    _lastRedirectUrl = location;
                    if (location.Contains("apisession="))
                    {
                        return location;
                    }
                    return await GetWithRedirectsAsync(location);
                }
            }

            return await response.Content.ReadAsStringAsync();
        }

        private string ExtractFormAction(string html)
        {
            var formPattern = @"<form[^>]*action=[""']([^""']+)[""']";
            var match = Regex.Match(html, formPattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private Dictionary<string, string> ExtractFormData(string html)
        {
            var formData = new Dictionary<string, string>();
            var inputPattern = @"<input[^>]*>";

            foreach (Match match in Regex.Matches(html, inputPattern, RegexOptions.IgnoreCase))
            {
                var inputHtml = match.Value;
                var nameMatch = Regex.Match(inputHtml, @"name=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var valueMatch = Regex.Match(inputHtml, @"value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                var typeMatch = Regex.Match(inputHtml, @"type=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

                var name = nameMatch.Success ? nameMatch.Groups[1].Value : null;
                var value = valueMatch.Success ? valueMatch.Groups[1].Value : "";
                var type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLower() : "text";

                if (!string.IsNullOrEmpty(name) && type != "submit" && type != "button")
                {
                    formData[name] = value;
                }
            }

            return formData;
        }

        private Dictionary<string, string> ExtractAllInputsById(string html)
        {
            var formData = new Dictionary<string, string>();
            var inputPattern = @"<input[^>]*>";

            foreach (Match match in Regex.Matches(html, inputPattern, RegexOptions.IgnoreCase))
            {
                var inputHtml = match.Value;
                var idMatch = Regex.Match(inputHtml, @"id=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var valueMatch = Regex.Match(inputHtml, @"value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);

                if (idMatch.Success)
                {
                    var id = idMatch.Groups[1].Value;
                    var value = valueMatch.Success ? valueMatch.Groups[1].Value : "";
                    formData[id] = value;
                }
            }

            return formData;
        }

        private string ExtractSessionToken(string urlOrHtml)
        {
            var match = Regex.Match(urlOrHtml, @"apisession=([^&\s""']+)");
            if (match.Success)
            {
                var token = match.Groups[1].Value;
                return token.Length > 8 ? token.Substring(0, 8) : token;
            }
            return null;
        }

        private string EncryptPasswordWithRsa(string password, string encKey)
        {
            var parts = encKey.Split('~');
            if (parts.Length != 2)
                throw new ArgumentException("Invalid encryption key format");

            var exponentHex = parts[0];
            var modulusHex = parts[1];

            var exponent = HexStringToBytes(exponentHex);
            var modulus = HexStringToBytes(modulusHex);

            var rsaParams = new RSAParameters
            {
                Exponent = exponent,
                Modulus = modulus
            };

            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(rsaParams);
                var passwordBytes = Encoding.UTF8.GetBytes(password);
                var encrypted = rsa.Encrypt(passwordBytes, false);

                var hexBuilder = new StringBuilder();
                foreach (var b in encrypted)
                {
                    hexBuilder.Append(b.ToString("x2"));
                }
                return hexBuilder.ToString();
            }
        }

        private byte[] HexStringToBytes(string hex)
        {
            hex = hex.Trim();
            if (hex.Length % 2 != 0)
                hex = "0" + hex;

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        private void RaiseStatus(string message, bool isError = false)
        {
            StatusChanged?.Invoke(this, new IciciTokenEventArgs(message, isError));
        }

        #endregion

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Result of ICICI token generation attempt
    /// </summary>
    public class IciciTokenResult
    {
        public bool Success { get; set; }
        public string SessionToken { get; set; }
        public string Error { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Event args for ICICI token generation status
    /// </summary>
    public class IciciTokenEventArgs : EventArgs
    {
        public string Message { get; }
        public bool IsError { get; }
        public DateTime Timestamp { get; }

        public IciciTokenEventArgs(string message, bool isError = false)
        {
            Message = message;
            IsError = isError;
            Timestamp = DateTime.Now;
        }
    }

}
