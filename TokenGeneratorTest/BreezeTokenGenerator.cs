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

namespace TokenGeneratorTest
{
    /// <summary>
    /// Automated ICICI Breeze token generation using HTTP-based flow.
    /// Attempts to replicate the Selenium-based login without browser automation.
    ///
    /// NOTE: ICICI's login flow is more restrictive than Zerodha's:
    /// - Zerodha exposes /api/login and /api/twofa JSON endpoints
    /// - ICICI uses traditional form-based login with server-side rendering
    /// - This class attempts HTTP-based login but may require adjustments
    ///   based on ICICI's actual API behavior
    /// </summary>
    public class BreezeTokenGenerator
    {
        // ICICI login endpoints
        private const string LOGIN_PAGE_URL = "https://api.icicidirect.com/apiuser/login";
        private const string TRADE_LOGIN_URL = "https://api.icicidirect.com/apiuser/tradelogin";

        // Credentials
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string _userId;
        private readonly string _password;
        private readonly string _totpSecret;

        // HTTP client with cookie support
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;

        /// <summary>
        /// Event raised when token generation status changes
        /// </summary>
        public event EventHandler<BreezeTokenEventArgs> StatusChanged;

        public BreezeTokenGenerator(string apiKey, string apiSecret, string userId,
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
        /// Attempt to generate session token using HTTP-based flow
        /// </summary>
        public async Task<BreezeTokenResult> GenerateTokenAsync()
        {
            try
            {
                RaiseStatus("Step 1: Fetching login page...");

                // Step 1: Get login page
                var loginUrl = $"{LOGIN_PAGE_URL}?api_key={Uri.EscapeDataString(_apiKey)}";
                var loginPageResponse = await GetWithRedirectsAsync(loginUrl);

                if (loginPageResponse.Contains("Public Key does not exist"))
                {
                    return new BreezeTokenResult
                    {
                        Success = false,
                        Error = "Invalid API Key - Public Key does not exist"
                    };
                }

                RaiseStatus($"Step 1: Got login page ({loginPageResponse.Length} chars)");

                // Debug: Show login page preview
                RaiseStatus($"Step 1: Page preview: {loginPageResponse.Substring(0, Math.Min(200, loginPageResponse.Length))}...");

                // Step 2: Extract form details
                RaiseStatus("Step 2: Extracting form details...");
                var formData = ExtractFormData(loginPageResponse);

                // Also extract form action
                var formAction = ExtractFormAction(loginPageResponse);
                RaiseStatus($"Step 2: Form action: {formAction ?? "none"}");
                RaiseStatus($"Step 2: Extracted {formData.Count} fields: {string.Join(", ", formData.Keys)}");

                if (formData == null || formData.Count == 0)
                {
                    // ICICI may use a different login mechanism
                    RaiseStatus("Step 2: No traditional form found - checking for SPA/AJAX login...");

                    // Try to find any __VIEWSTATE or hidden fields
                    var viewState = ExtractAspNetFields(loginPageResponse);
                    if (viewState.Count > 0)
                    {
                        RaiseStatus($"Step 2: Found ASP.NET fields: {string.Join(", ", viewState.Keys)}");
                        formData = viewState;
                    }
                }

                // Step 3: Submit login credentials
                RaiseStatus("Step 3: Submitting credentials...");

                // Try common field name patterns for user ID
                formData["txtuid"] = _userId;
                formData["txtPass"] = _password;
                formData["ctl00$ContentPlaceHolder1$txtuid"] = _userId;
                formData["ctl00$ContentPlaceHolder1$txtPass"] = _password;
                formData["User/Login Id"] = _userId;
                formData["Password"] = _password;

                // Use extracted form action or default
                var postUrl = formAction ?? TRADE_LOGIN_URL;
                if (!postUrl.StartsWith("http"))
                {
                    postUrl = "https://api.icicidirect.com/apiuser/" + postUrl;
                }

                RaiseStatus($"Step 3: Posting to {postUrl}...");

                // Try the form post
                var loginResponse = await PostFormAsync(postUrl, formData, loginUrl);

                RaiseStatus($"Step 3: Got response ({loginResponse.Length} chars)");

                // Debug: Show login response preview
                RaiseStatus($"Step 3: Response preview: {loginResponse.Substring(0, Math.Min(300, loginResponse.Length))}...");

                // Check if this is the actual login page (contains login form fields)
                bool isLoginPage = loginResponse.Contains("User/Login Id") ||
                                   loginResponse.Contains("txtuid") ||
                                   loginResponse.Contains("Password");

                // Track the form data we submit for OTP step
                Dictionary<string, string> submittedFormData = null;

                if (isLoginPage)
                {
                    RaiseStatus("Step 3.5: This is the login form page - need to submit credentials...");

                    // Save the login page HTML for analysis
                    try
                    {
                        File.WriteAllText("login_page.html", loginResponse);
                        RaiseStatus("Step 3.5: Saved login page to login_page.html for analysis");
                    }
                    catch { }

                    // Extract the login form with debug
                    RaiseStatus("Step 3.5: Extracting form fields with debug...");
                    var loginFormData = ExtractFormData(loginResponse, debug: true);
                    var loginFormAction = ExtractFormAction(loginResponse) ?? "tradelogin";

                    RaiseStatus($"Step 3.5: Login form has {loginFormData.Count} fields");

                    // Extract hidden field values by ID (some fields have id but no name)
                    var hidpValue = ExtractHiddenFieldValueById(loginResponse, "hidp");
                    var hiddobValue = ExtractHiddenFieldValueById(loginResponse, "hiddob");
                    RaiseStatus($"Step 3.5: hidp value: '{hidpValue}', hiddob value: '{hiddobValue}'");

                    // Get encryption key from the page
                    var encKeyMatch = Regex.Match(loginResponse, @"id=[""']hidenc[""'][^>]*value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    if (!encKeyMatch.Success)
                    {
                        encKeyMatch = Regex.Match(loginResponse, @"value=[""']([^""']*)[""'][^>]*id=[""']hidenc[""']", RegexOptions.IgnoreCase);
                    }
                    var encKey = encKeyMatch.Success ? encKeyMatch.Groups[1].Value : "";
                    RaiseStatus($"Step 3.5: Encryption key (hidenc): {(encKey.Length > 50 ? encKey.Substring(0, 50) + "..." : encKey)}");

                    // Try to find the public key (hidplk)
                    var plkMatch = Regex.Match(loginResponse, @"id=[""']hidplk[""'][^>]*value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                    if (!plkMatch.Success)
                    {
                        plkMatch = Regex.Match(loginResponse, @"value=[""']([^""']*)[""'][^>]*id=[""']hidplk[""']", RegexOptions.IgnoreCase);
                    }
                    var publicKey = plkMatch.Success ? plkMatch.Groups[1].Value : "";

                    // Look for JavaScript that handles the form submission
                    var jsMatch = Regex.Match(loginResponse, @"function\s+\w*[Ss]ubmit\w*\s*\([^)]*\)\s*\{([^}]+)\}", RegexOptions.Singleline);
                    if (jsMatch.Success)
                    {
                        RaiseStatus($"Step 3.5: Found JS submit function: {jsMatch.Value.Substring(0, Math.Min(200, jsMatch.Value.Length))}...");
                    }

                    // Look for onclick handler on button
                    var btnMatch = Regex.Match(loginResponse, @"id=[""']btnSubmit[""'][^>]*onclick=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                    if (btnMatch.Success)
                    {
                        RaiseStatus($"Step 3.5: Button onclick: {btnMatch.Groups[1].Value}");
                    }

                    // ICICI encrypts the password using RSA with the public key
                    // The format is: exponent~modulus (both in hex)
                    string encryptedPassword = _password;
                    if (!string.IsNullOrEmpty(encKey) && encKey.Contains("~"))
                    {
                        try
                        {
                            encryptedPassword = EncryptPasswordWithRsa(_password, encKey);
                            RaiseStatus($"Step 3.5: Encrypted password length: {encryptedPassword.Length}");
                        }
                        catch (Exception ex)
                        {
                            RaiseStatus($"Step 3.5: RSA encryption failed: {ex.Message}");
                        }
                    }

                    // Based on the JavaScript analysis:
                    // 1. The form posts to /tradelogin/getotp (not /tradelogin)
                    // 2. The encrypted password goes into 'hidp' field
                    // 3. The input fields txtuid/txtPass don't have 'name' attribute - only 'fieldname' for validation
                    // 4. PostForm() function (from jscomm.js) serializes the form - need to understand how

                    // Build form data exactly as serializeJSON() does it:
                    // - It iterates through all input/select/textarea elements in the form
                    // - Uses element ID as the key (not name!)
                    // - Stores element value as the value

                    var submitFormData = new Dictionary<string, string>();

                    // Extract ALL inputs by ID (not just those with name attributes)
                    var allInputs = ExtractAllInputsById(loginResponse);
                    foreach (var kvp in allInputs)
                    {
                        submitFormData[kvp.Key] = kvp.Value;
                    }

                    // Now set the values exactly as the JavaScript would:
                    // 1. txtuid = user input
                    // 2. txtPass = "************" (masked by JS after encryption)
                    // 3. hidp = encrypted password (set by $('#hidp').val(cmdEncrypt(...)))
                    // 4. chkssTnc = "Y" (if checkbox checked)
                    // 5. hiddob = "" (not used in this flow)

                    submitFormData["txtuid"] = _userId;
                    submitFormData["txtPass"] = "************";  // JS masks password with asterisks
                    submitFormData["hidp"] = encryptedPassword;  // Encrypted password goes here
                    submitFormData["chkssTnc"] = "Y";  // Checkbox value
                    submitFormData["hiddob"] = "";
                    submitFormData["btnSubmit"] = "Login";  // Button value might be needed

                    // The JS sets: $('#hidp').val(cmdEncrypt($.trim($('#txtPass').val())));
                    // So we need hidp = encrypted password

                    RaiseStatus($"Step 3.5: Submitting with {submitFormData.Count} fields");

                    // Debug: show what we're sending (except sensitive data)
                    foreach (var kvp in submitFormData)
                    {
                        var valuePreview = kvp.Key.Contains("hid") && kvp.Value.Length > 30
                            ? kvp.Value.Substring(0, 30) + "..."
                            : kvp.Value;
                        if (kvp.Key == "hidp" || kvp.Key == "txtPass")
                            valuePreview = "[MASKED]";
                        RaiseStatus($"    {kvp.Key} = {valuePreview}");
                    }

                    // Try the /getotp endpoint first with form data
                    var getOtpUrl = "https://api.icicidirect.com/apiuser/tradelogin/getotp";
                    RaiseStatus($"Step 3.5: Posting form to {getOtpUrl}...");

                    loginResponse = await PostFormAsync(getOtpUrl, submitFormData, TRADE_LOGIN_URL);
                    RaiseStatus($"Step 3.5: Form response ({loginResponse.Length} chars)");

                    // If form-encoded failed, try JSON format (since JS uses datatype: "json")
                    if (loginResponse.Contains("Invalid request") || loginResponse.Contains("pageerror"))
                    {
                        RaiseStatus("Step 3.5: Form post failed, trying JSON format...");
                        loginResponse = await PostJsonAsync(getOtpUrl, submitFormData, TRADE_LOGIN_URL);
                        RaiseStatus($"Step 3.5: JSON response ({loginResponse.Length} chars)");
                    }

                    // If JSON also failed, try posting to the base tradelogin first then getotp
                    if (loginResponse.Contains("Invalid request") || loginResponse.Contains("pageerror"))
                    {
                        RaiseStatus("Step 3.5: JSON failed, trying tradelogin then getotp...");

                        // First, post to tradelogin (regular form submission)
                        loginResponse = await PostFormAsync(TRADE_LOGIN_URL, submitFormData, TRADE_LOGIN_URL);
                        RaiseStatus($"Step 3.5: tradelogin response ({loginResponse.Length} chars)");

                        // If that worked and didn't go to pageerror, try getotp again
                        if (!loginResponse.Contains("pageerror") && !loginResponse.Contains("Invalid request"))
                        {
                            var newFormData = ExtractFormData(loginResponse);
                            foreach (var kvp in submitFormData)
                            {
                                if (!newFormData.ContainsKey(kvp.Key))
                                    newFormData[kvp.Key] = kvp.Value;
                            }
                            loginResponse = await PostFormAsync(getOtpUrl, newFormData, TRADE_LOGIN_URL);
                            RaiseStatus($"Step 3.5: getotp retry response ({loginResponse.Length} chars)");
                        }
                    }

                    RaiseStatus($"Step 3.5: Preview: {loginResponse.Substring(0, Math.Min(500, loginResponse.Length))}...");

                    // Save the response for analysis
                    try
                    {
                        File.WriteAllText("otp_page.html", loginResponse);
                        RaiseStatus("Step 3.5: Saved response to otp_page.html");
                    }
                    catch { }

                    // Store the form data for OTP step
                    submittedFormData = submitFormData;
                }

                // Check if we need to handle TOTP
                if (loginResponse.Contains("pnlOTP") || loginResponse.Contains("hiotp") || loginResponse.Contains("Verify TOTP"))
                {
                    RaiseStatus("Step 4: TOTP page detected, generating TOTP...");

                    var totp = GenerateTotp();
                    RaiseStatus($"Step 4: Generated TOTP: {totp}");

                    // The OTP page is a fragment returned by the AJAX call
                    // It contains hidden fields that need to be extracted by ID
                    // The JS function submitotp() does:
                    // 1. Collects OTP digits from input[tg-nm=otp]
                    // 2. Sets $('#hiotp').val(_opt)
                    // 3. Posts to /tradelogin/validateuser

                    // Extract all fields from OTP page by ID
                    var otpFormData = ExtractAllInputsById(loginResponse);
                    RaiseStatus($"Step 4: Extracted {otpFormData.Count} fields from OTP page: {string.Join(", ", otpFormData.Keys)}");

                    // Set the OTP value in hiotp field (as the JS would)
                    otpFormData["hiotp"] = totp;

                    // We also need to include the login form fields (preserved in session/cookies)
                    // Add back the fields from the original login form submission
                    if (submittedFormData != null)
                    {
                        foreach (var kvp in submittedFormData)
                        {
                            if (!otpFormData.ContainsKey(kvp.Key))
                                otpFormData[kvp.Key] = kvp.Value;
                        }
                    }

                    RaiseStatus($"Step 4: OTP form has {otpFormData.Count} fields total");

                    // The validateuser endpoint
                    var validateUrl = "https://api.icicidirect.com/apiuser/tradelogin/validateuser";
                    RaiseStatus($"Step 4: Submitting TOTP to {validateUrl}...");

                    var totpResponse = await PostFormAsync(validateUrl, otpFormData, TRADE_LOGIN_URL);

                    RaiseStatus($"Step 4: TOTP response ({totpResponse.Length} chars)");

                    // Check for apisession in response or redirect
                    if (totpResponse.Contains("apisession="))
                    {
                        var sessionToken = ExtractSessionToken(totpResponse);
                        RaiseStatus($"Step 4: Found session token in TOTP response!");
                        return new BreezeTokenResult
                        {
                            Success = true,
                            SessionToken = sessionToken
                        };
                    }

                    // Check if TOTP response has another redirect
                    if (_lastRedirectUrl != null && _lastRedirectUrl.Contains("apisession="))
                    {
                        var sessionToken = ExtractSessionToken(_lastRedirectUrl);
                        RaiseStatus($"Step 4: Found session token in redirect URL!");
                        return new BreezeTokenResult
                        {
                            Success = true,
                            SessionToken = sessionToken
                        };
                    }

                    // Save response for debugging
                    try
                    {
                        File.WriteAllText("validate_response.html", totpResponse);
                        RaiseStatus("Step 4: Saved response to validate_response.html");
                    }
                    catch { }

                    // Debug: show first 500 chars of response
                    RaiseStatus($"Step 4: Response preview: {totpResponse.Substring(0, Math.Min(500, totpResponse.Length))}...");
                }

                // Check final URL for apisession
                var finalUrl = GetLastRedirectUrl();
                if (!string.IsNullOrEmpty(finalUrl) && finalUrl.Contains("apisession="))
                {
                    var sessionToken = ExtractSessionToken(finalUrl);
                    return new BreezeTokenResult
                    {
                        Success = true,
                        SessionToken = sessionToken
                    };
                }

                return new BreezeTokenResult
                {
                    Success = false,
                    Error = "Could not complete login flow - apisession not found. ICICI may require Selenium-based automation."
                };
            }
            catch (Exception ex)
            {
                RaiseStatus($"Error: {ex.Message}", true);
                return new BreezeTokenResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        private string _lastRedirectUrl;

        private async Task<string> GetWithRedirectsAsync(string url, int maxRedirects = 10)
        {
            // Ensure we have an absolute URL
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

                    // Check if redirect contains apisession
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
            // Ensure we have an absolute URL
            if (!url.StartsWith("http"))
            {
                url = "https://api.icicidirect.com" + (url.StartsWith("/") ? "" : "/") + url;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new FormUrlEncodedContent(formData);
            request.Headers.Add("Referer", referer);

            var response = await _httpClient.SendAsync(request);

            // Handle redirect
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                var location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    // Make absolute if needed
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

        private async Task<string> PostJsonAsync(string url, Dictionary<string, string> formData, string referer)
        {
            // Ensure we have an absolute URL
            if (!url.StartsWith("http"))
            {
                url = "https://api.icicidirect.com" + (url.StartsWith("/") ? "" : "/") + url;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            var json = JsonConvert.SerializeObject(formData);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("Referer", referer);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");  // AJAX header

            var response = await _httpClient.SendAsync(request);

            // Handle redirect
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

        private string GetLastRedirectUrl()
        {
            return _lastRedirectUrl;
        }

        private string ExtractFormAction(string html)
        {
            // Extract form action URL
            var formPattern = @"<form[^>]*action=[""']([^""']+)[""']";
            var match = Regex.Match(html, formPattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            return null;
        }

        private string ExtractHiddenFieldValueById(string html, string fieldId)
        {
            // Try id before value
            var pattern = $@"id=[""']{fieldId}[""'][^>]*value=[""']([^""']*)[""']";
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Try value before id
            pattern = $@"value=[""']([^""']*)[""'][^>]*id=[""']{fieldId}[""']";
            match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        private Dictionary<string, string> ExtractFormData(string html, bool debug = false)
        {
            var formData = new Dictionary<string, string>();

            // Extract all input fields
            var inputPattern = @"<input[^>]*>";
            foreach (Match match in Regex.Matches(html, inputPattern, RegexOptions.IgnoreCase))
            {
                var inputHtml = match.Value;
                var nameMatch = Regex.Match(inputHtml, @"name=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var valueMatch = Regex.Match(inputHtml, @"value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                var typeMatch = Regex.Match(inputHtml, @"type=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var idMatch = Regex.Match(inputHtml, @"id=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var placeholderMatch = Regex.Match(inputHtml, @"placeholder=[""']([^""']*)[""']", RegexOptions.IgnoreCase);

                var name = nameMatch.Success ? nameMatch.Groups[1].Value : null;
                var value = valueMatch.Success ? valueMatch.Groups[1].Value : "";
                var type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLower() : "text";
                var id = idMatch.Success ? idMatch.Groups[1].Value : null;
                var placeholder = placeholderMatch.Success ? placeholderMatch.Groups[1].Value : null;

                if (debug)
                {
                    RaiseStatus($"    Input: name={name}, id={id}, type={type}, placeholder={placeholder}");
                }

                if (!string.IsNullOrEmpty(name))
                {
                    // Skip submit buttons and certain types
                    if (type != "submit" && type != "button" && type != "image")
                    {
                        formData[name] = value;
                    }
                }
            }

            return formData;
        }

        /// <summary>
        /// Extract all input/select/textarea elements by their ID (matching serializeJSON behavior)
        /// </summary>
        private Dictionary<string, string> ExtractAllInputsById(string html)
        {
            var formData = new Dictionary<string, string>();

            // Extract input fields by ID
            var inputPattern = @"<input[^>]*>";
            foreach (Match match in Regex.Matches(html, inputPattern, RegexOptions.IgnoreCase))
            {
                var inputHtml = match.Value;
                var idMatch = Regex.Match(inputHtml, @"id=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var valueMatch = Regex.Match(inputHtml, @"value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
                var typeMatch = Regex.Match(inputHtml, @"type=[""']([^""']+)[""']", RegexOptions.IgnoreCase);

                if (!idMatch.Success)
                    continue;

                var id = idMatch.Groups[1].Value;
                var value = valueMatch.Success ? valueMatch.Groups[1].Value : "";
                var type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLower() : "text";

                // serializeJSON includes ALL elements with IDs (even buttons)
                formData[id] = value;
            }

            // Extract select elements
            var selectPattern = @"<select[^>]*id=[""']([^""']+)[""'][^>]*>.*?</select>";
            foreach (Match match in Regex.Matches(html, selectPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var id = match.Groups[1].Value;
                // Extract selected option value
                var selectedMatch = Regex.Match(match.Value, @"<option[^>]*selected[^>]*value=[""']([^""']*)[""']");
                var value = selectedMatch.Success ? selectedMatch.Groups[1].Value : "";
                formData[id] = value;
            }

            // Extract textarea elements
            var textareaPattern = @"<textarea[^>]*id=[""']([^""']+)[""'][^>]*>(.*?)</textarea>";
            foreach (Match match in Regex.Matches(html, textareaPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var id = match.Groups[1].Value;
                var value = match.Groups[2].Value;
                formData[id] = value;
            }

            return formData;
        }

        private Dictionary<string, string> ExtractAspNetFields(string html)
        {
            var fields = new Dictionary<string, string>();

            // Extract __VIEWSTATE, __VIEWSTATEGENERATOR, __EVENTVALIDATION, etc.
            var patterns = new[] { "__VIEWSTATE", "__VIEWSTATEGENERATOR", "__EVENTVALIDATION", "__EVENTTARGET", "__EVENTARGUMENT" };

            foreach (var fieldName in patterns)
            {
                var pattern = $@"<input[^>]*name=[""']{fieldName}[""'][^>]*value=[""']([^""']*)[""']";
                var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    fields[fieldName] = match.Groups[1].Value;
                }
                else
                {
                    // Try alternate order (value before name)
                    pattern = $@"<input[^>]*value=[""']([^""']*)[""'][^>]*name=[""']{fieldName}[""']";
                    match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        fields[fieldName] = match.Groups[1].Value;
                    }
                }
            }

            return fields;
        }

        private string ExtractSessionToken(string urlOrHtml)
        {
            var match = Regex.Match(urlOrHtml, @"apisession=([^&\s""']+)");
            if (match.Success)
            {
                var token = match.Groups[1].Value;
                // Session token is typically 8 characters
                return token.Length > 8 ? token.Substring(0, 8) : token;
            }
            return null;
        }

        /// <summary>
        /// Generate TOTP code using RFC 6238
        /// </summary>
        private string GenerateTotp()
        {
            return TotpGenerator.GenerateTotp(_totpSecret);
        }

        /// <summary>
        /// Encrypt password using RSA with PKCS#1 v1.5 padding
        /// The encKey format is: exponent~modulus (both in hex)
        /// Returns: hex string (as expected by ICICI's RSA.js)
        /// </summary>
        private string EncryptPasswordWithRsa(string password, string encKey)
        {
            // Parse the encryption key (format: exponent~modulus)
            var parts = encKey.Split('~');
            if (parts.Length != 2)
                throw new ArgumentException("Invalid encryption key format");

            var exponentHex = parts[0];
            var modulusHex = parts[1];

            // Convert hex to bytes
            var exponent = HexStringToBytes(exponentHex);
            var modulus = HexStringToBytes(modulusHex);

            // Create RSA parameters
            var rsaParams = new RSAParameters
            {
                Exponent = exponent,
                Modulus = modulus
            };

            // Encrypt
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(rsaParams);
                var passwordBytes = Encoding.UTF8.GetBytes(password);
                var encrypted = rsa.Encrypt(passwordBytes, false); // false = PKCS#1 v1.5

                // The JavaScript RSA.js returns hex format, not base64
                // Convert to hex string (lowercase, no separators)
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
            // Remove any leading zeros or spaces
            hex = hex.Trim();

            // Ensure even length
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
            StatusChanged?.Invoke(this, new BreezeTokenEventArgs(message, isError));
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Result of Breeze token generation attempt
    /// </summary>
    public class BreezeTokenResult
    {
        public bool Success { get; set; }
        public string SessionToken { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Event args for Breeze token generation status
    /// </summary>
    public class BreezeTokenEventArgs : EventArgs
    {
        public string Message { get; }
        public bool IsError { get; }
        public DateTime Timestamp { get; }

        public BreezeTokenEventArgs(string message, bool isError = false)
        {
            Message = message;
            IsError = isError;
            Timestamp = DateTime.Now;
        }
    }
}
