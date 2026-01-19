using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarMcp.Core.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Response from Google's device code endpoint
/// </summary>
internal class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_url")]
    public string VerificationUrl { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

/// <summary>
/// Response from Google's token endpoint
/// </summary>
internal class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Google authentication service using OAuth 2.0 with per-account token caching
/// </summary>
public class GoogleAuthenticationService : IGoogleAuthenticationService
{
    private readonly ILogger<GoogleAuthenticationService> _logger;

    public GoogleAuthenticationService(ILogger<GoogleAuthenticationService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> AuthenticateInteractiveAsync(
        string clientId,
        string clientSecret,
        string[] scopes,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting interactive Google authentication for account {AccountId}...", accountId);
            _logger.LogInformation("A browser window will open for you to sign in.");

            var secrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            };

            var credPath = GetCredentialPath(accountId);
            _logger.LogDebug("Token cache path: {CredPath}", credPath);

            // Use "user" as the user identifier since we're isolating by accountId directory
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                scopes,
                "user",
                cancellationToken,
                new FileDataStore(credPath, true)
            );

            _logger.LogInformation("✓ Interactive Google authentication successful for account {AccountId}", accountId);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Google authentication cancelled for account {AccountId}", accountId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google authentication failed for account {AccountId}: {Message}", accountId, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HasValidCredentialAsync(
        string clientId,
        string clientSecret,
        string[] scopes,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var credPath = GetCredentialPath(accountId);
            
            // Check if token file exists
            var tokenFile = Path.Combine(credPath, "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user");
            if (!File.Exists(tokenFile))
            {
                _logger.LogDebug("No cached credential found for Google account {AccountId}", accountId);
                return false;
            }

            var secrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            };

            _logger.LogDebug("Checking cached credential for Google account {AccountId}...", accountId);

            // Try to load existing credential - this will refresh if needed
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                scopes,
                "user",
                cancellationToken,
                new FileDataStore(credPath, true)
            );

            // Check if the token is valid (not expired or can be refreshed)
            if (credential.Token.IsStale)
            {
                // Try to refresh
                var refreshed = await credential.RefreshTokenAsync(cancellationToken);
                if (!refreshed)
                {
                    _logger.LogWarning("Failed to refresh Google token for account {AccountId}", accountId);
                    return false;
                }
            }

            _logger.LogDebug("✓ Valid Google credential found for account {AccountId}", accountId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error checking Google credential for account {AccountId}: {Message}", accountId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get the credential storage path for a specific account
    /// </summary>
    private static string GetCredentialPath(string accountId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalendarMcp",
            "google",
            accountId
        );
    }

    /// <inheritdoc/>
    public async Task<bool> AuthenticateWithDeviceCodeAsync(
        string clientId,
        string clientSecret,
        string[] scopes,
        string accountId,
        Func<string, string, Task> deviceCodeCallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting device code authentication for account {AccountId}...", accountId);

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30) // Prevent hanging on network issues
            };

            // Step 1: Request device code
            var deviceCodeRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = string.Join(" ", scopes)
            });

            var deviceCodeResponse = await httpClient.PostAsync(
                "https://oauth2.googleapis.com/device/code",
                deviceCodeRequest,
                cancellationToken);

            if (!deviceCodeResponse.IsSuccessStatusCode)
            {
                var errorBody = await deviceCodeResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Device code request failed with status {StatusCode}: {ErrorBody}",
                    deviceCodeResponse.StatusCode,
                    errorBody);

                // Provide user-friendly error messages for common failures
                if (deviceCodeResponse.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    _logger.LogError("This may indicate an invalid client ID or misconfigured OAuth application");
                }
                else if (deviceCodeResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogError("Rate limited by Google. Please wait before trying again");
                }

                return false;
            }

            var deviceCode = await deviceCodeResponse.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: cancellationToken);
            if (deviceCode == null)
            {
                _logger.LogError("Failed to parse device code response");
                return false;
            }

            // Step 2: Display code to user
            await deviceCodeCallback(deviceCode.VerificationUrl, deviceCode.UserCode);

            // Step 3: Poll for token
            var pollInterval = TimeSpan.FromSeconds(deviceCode.Interval > 0 ? deviceCode.Interval : 5);
            var expiresAt = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);

            while (DateTime.UtcNow < expiresAt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(pollInterval, cancellationToken);

                var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["device_code"] = deviceCode.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });

                var tokenResponse = await httpClient.PostAsync(
                    "https://oauth2.googleapis.com/token",
                    tokenRequest,
                    cancellationToken);

                var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
                if (token == null)
                {
                    continue;
                }

                if (token.Error == "authorization_pending")
                {
                    // User hasn't authorized yet, keep polling
                    continue;
                }

                if (token.Error == "slow_down")
                {
                    // Increase polling interval
                    pollInterval = pollInterval.Add(TimeSpan.FromSeconds(5));
                    continue;
                }

                if (token.Error == "access_denied")
                {
                    // User explicitly denied authorization
                    _logger.LogWarning("Authorization denied by user for account {AccountId}", accountId);
                    return false;
                }

                if (token.Error == "expired_token")
                {
                    // Device code expired before user authorized
                    _logger.LogWarning("Device code expired before authorization for account {AccountId}", accountId);
                    return false;
                }

                if (!string.IsNullOrEmpty(token.Error))
                {
                    _logger.LogError("Token request failed with error '{Error}' for account {AccountId}", token.Error, accountId);
                    return false;
                }

                if (!string.IsNullOrEmpty(token.AccessToken) && !string.IsNullOrEmpty(token.RefreshToken))
                {
                    // Success! Save the token
                    await SaveTokenAsync(accountId, token, cancellationToken);
                    _logger.LogInformation("✓ Device code authentication successful for account {AccountId}", accountId);
                    return true;
                }
            }

            _logger.LogError("Device code expired for account {AccountId}", accountId);
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError("Network timeout during device code authentication for account {AccountId}", accountId);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Device code authentication cancelled for account {AccountId}", accountId);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error during device code authentication for account {AccountId}: {Message}", accountId, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device code authentication failed for account {AccountId}: {Message}", accountId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Save token in Google API format for compatibility with GoogleWebAuthorizationBroker
    /// </summary>
    private async Task SaveTokenAsync(string accountId, TokenResponse token, CancellationToken cancellationToken)
    {
        var credPath = GetCredentialPath(accountId);
        Directory.CreateDirectory(credPath);

        // Save in the format expected by Google.Apis.Auth
        var tokenFile = Path.Combine(credPath, "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user");

        var tokenData = new
        {
            access_token = token.AccessToken,
            refresh_token = token.RefreshToken,
            token_type = token.TokenType ?? "Bearer",
            expires_in = token.ExpiresIn,
            issued_utc = DateTime.UtcNow.ToString("o"),
            Issued = DateTime.UtcNow.ToString("o")
        };

        var json = JsonSerializer.Serialize(tokenData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tokenFile, json, cancellationToken);

        _logger.LogDebug("Saved token to {TokenFile}", tokenFile);
    }
}
