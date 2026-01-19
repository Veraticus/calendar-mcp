# HTTP Server with OAuth Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add HTTP transport with Cloudflare Access OAuth and Google device code flow to calendar-mcp.

**Architecture:** New ASP.NET Core project (CalendarMcp.HttpServer) using ModelContextProtocol.AspNetCore for HTTP transport. OIDC middleware authenticates via Cloudflare Access when configured, otherwise runs in dev mode. Google device code flow added to CLI for headless setup.

**Tech Stack:** .NET 9, ASP.NET Core, ModelContextProtocol.AspNetCore, Microsoft.AspNetCore.Authentication.OpenIdConnect, Google OAuth 2.0 Device Flow

---

## Task 1: Create HttpServer Project Structure

**Files:**
- Create: `src/CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj`
- Modify: `src/calendar-mcp.slnx`

**Step 1: Create the project file**

Create `src/CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="0.4.1-preview.1" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="9.0.0" />
    <PackageReference Include="Serilog.AspNetCore" Version="8.0.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\CalendarMcp.Core\CalendarMcp.Core.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Add project to solution**

Modify `src/calendar-mcp.slnx` to add the new project:

```xml
<Solution>
  <Project Path="CalendarMcp.Cli/CalendarMcp.Cli.csproj" />
  <Project Path="CalendarMcp.Core/CalendarMcp.Core.csproj" />
  <Project Path="CalendarMcp.StdioServer/CalendarMcp.StdioServer.csproj" />
  <Project Path="CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj" />
</Solution>
```

**Step 3: Verify project structure**

Run: `cd ~/Personal/calendar-mcp/src && dotnet restore`
Expected: Restore succeeds with no errors

**Step 4: Commit**

```bash
git add src/CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj src/calendar-mcp.slnx
git commit -m "feat: add CalendarMcp.HttpServer project structure"
```

---

## Task 2: Implement Basic HTTP Server (No Auth)

**Files:**
- Create: `src/CalendarMcp.HttpServer/Program.cs`

**Step 1: Create the HTTP server Program.cs**

Create `src/CalendarMcp.HttpServer/Program.cs`:

```csharp
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Tools;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Serilog;

namespace CalendarMcp.HttpServer;

public class Program
{
    public static void Main(string[] args)
    {
        // Configure Serilog
        var logDir = ConfigurationPaths.GetLogDirectory();
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "calendar-mcp-http-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            // Use Serilog
            builder.Host.UseSerilog();

            // Load configuration from standard paths
            var configPath = ConfigurationPaths.GetConfigFilePath();
            if (File.Exists(configPath))
            {
                builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);
                Log.Information("Loaded configuration from {ConfigPath}", configPath);
            }
            else
            {
                Log.Warning("No configuration file found at {ConfigPath}", configPath);
            }

            // Add environment variable overrides
            builder.Configuration.AddEnvironmentVariables("CALENDAR_MCP_");

            // Configure Calendar MCP settings
            builder.Services.Configure<CalendarMcpConfiguration>(
                builder.Configuration.GetSection("CalendarMcp"));

            // Add Calendar MCP core services
            builder.Services.AddCalendarMcpCore();

            // Check for OAuth configuration
            var accessClientId = Environment.GetEnvironmentVariable("ACCESS_CLIENT_ID");
            var accessClientSecret = Environment.GetEnvironmentVariable("ACCESS_CLIENT_SECRET");
            var accessConfigUrl = Environment.GetEnvironmentVariable("ACCESS_CONFIG_URL");
            var authEnabled = !string.IsNullOrEmpty(accessClientId) &&
                              !string.IsNullOrEmpty(accessClientSecret) &&
                              !string.IsNullOrEmpty(accessConfigUrl);

            if (authEnabled)
            {
                Log.Information("OAuth authentication enabled via Cloudflare Access");

                builder.Services.AddAuthentication(options =>
                {
                    options.DefaultScheme = "Cookies";
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie("Cookies")
                .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.MetadataAddress = accessConfigUrl;
                    options.ClientId = accessClientId;
                    options.ClientSecret = accessClientSecret;
                    options.ResponseType = "code";
                    options.SaveTokens = true;
                    options.GetClaimsFromUserInfoEndpoint = true;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("email");
                    options.Scope.Add("profile");
                });

                builder.Services.AddAuthorization();
            }
            else
            {
                Log.Information("OAuth authentication disabled - running in dev mode");
            }

            // Add MCP server with HTTP transport
            builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithTools<ListAccountsTool>()
                .WithTools<GetEmailsTool>()
                .WithTools<SearchEmailsTool>()
                .WithTools<GetContextualEmailSummaryTool>()
                .WithTools<ListCalendarsTool>()
                .WithTools<GetCalendarEventsTool>()
                .WithTools<SendEmailTool>()
                .WithTools<CreateEventTool>();

            var app = builder.Build();

            // Configure middleware
            if (authEnabled)
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapMcp().RequireAuthorization();
            }
            else
            {
                app.MapMcp();
            }

            // Get host/port from environment
            var host = Environment.GetEnvironmentVariable("MCP_SERVER_HOST") ?? "0.0.0.0";
            var port = Environment.GetEnvironmentVariable("MCP_SERVER_PORT") ?? "8000";
            var url = $"http://{host}:{port}";

            Log.Information("Starting Calendar MCP HTTP server on {Url}", url);
            app.Run(url);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
```

**Step 2: Verify it builds**

Run: `cd ~/Personal/calendar-mcp/src && dotnet build CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj`
Expected: Build succeeds

**Step 3: Test dev mode startup**

Run: `cd ~/Personal/calendar-mcp/src && dotnet run --project CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj`
Expected: Server starts, logs "OAuth authentication disabled - running in dev mode"

Press Ctrl+C to stop.

**Step 4: Commit**

```bash
git add src/CalendarMcp.HttpServer/Program.cs
git commit -m "feat: implement basic HTTP server with dev mode"
```

---

## Task 3: Add Google Device Code Flow Interface

**Files:**
- Modify: `src/CalendarMcp.Core/Services/IGoogleAuthenticationService.cs`

**Step 1: Add device code method to interface**

Add to `src/CalendarMcp.Core/Services/IGoogleAuthenticationService.cs` after the existing methods:

```csharp
    /// <summary>
    /// Authenticate using device code flow (for headless/SSH scenarios)
    /// </summary>
    /// <param name="clientId">Google OAuth client ID</param>
    /// <param name="clientSecret">Google OAuth client secret</param>
    /// <param name="scopes">Required scopes</param>
    /// <param name="accountId">Unique account identifier for token cache</param>
    /// <param name="deviceCodeCallback">Callback to display device code info to user (verificationUrl, userCode)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if authentication was successful</returns>
    Task<bool> AuthenticateWithDeviceCodeAsync(
        string clientId,
        string clientSecret,
        string[] scopes,
        string accountId,
        Func<string, string, Task> deviceCodeCallback,
        CancellationToken cancellationToken = default);
```

**Step 2: Verify it builds**

Run: `cd ~/Personal/calendar-mcp/src && dotnet build CalendarMcp.Core/CalendarMcp.Core.csproj`
Expected: Build fails (interface not implemented) - this is expected, we'll implement next

**Step 3: Commit**

```bash
git add src/CalendarMcp.Core/Services/IGoogleAuthenticationService.cs
git commit -m "feat: add device code flow method to IGoogleAuthenticationService"
```

---

## Task 4: Implement Google Device Code Flow

**Files:**
- Modify: `src/CalendarMcp.Core/Providers/GoogleAuthenticationService.cs`

**Step 1: Add required usings**

Add to top of `src/CalendarMcp.Core/Providers/GoogleAuthenticationService.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
```

**Step 2: Add device code response models**

Add inside the namespace, before the class:

```csharp
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
```

**Step 3: Implement device code flow method**

Add to `GoogleAuthenticationService` class:

```csharp
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

            using var httpClient = new HttpClient();

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

            deviceCodeResponse.EnsureSuccessStatusCode();

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

                if (!string.IsNullOrEmpty(token.Error))
                {
                    _logger.LogError("Token request failed: {Error}", token.Error);
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
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Device code authentication cancelled for account {AccountId}", accountId);
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
```

**Step 4: Verify it builds**

Run: `cd ~/Personal/calendar-mcp/src && dotnet build`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/CalendarMcp.Core/Providers/GoogleAuthenticationService.cs
git commit -m "feat: implement Google device code flow authentication"
```

---

## Task 5: Add Device Code CLI Command

**Files:**
- Modify: `src/CalendarMcp.Cli/Commands/AddGoogleAccountCommand.cs`

**Step 1: Add device-code option to Settings**

In `AddGoogleAccountCommand.Settings` class, add:

```csharp
        [Description("Use device code flow for headless authentication (no browser required)")]
        [CommandOption("--device-code")]
        public bool DeviceCode { get; init; }
```

**Step 2: Update ExecuteAsync to support device code**

Replace the authentication try block (around line 116-133) with:

```csharp
        try
        {
            bool success;

            if (settings.DeviceCode)
            {
                // Device code flow for headless environments
                AnsiConsole.MarkupLine("[yellow]Using device code flow (headless mode)[/]");
                AnsiConsole.WriteLine();

                success = await _authService.AuthenticateWithDeviceCodeAsync(
                    clientId,
                    clientSecret,
                    scopes,
                    accountId,
                    async (verificationUrl, userCode) =>
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.Write(new Panel(
                            $"[bold]1.[/] Visit: [link={verificationUrl}]{verificationUrl}[/]\n" +
                            $"[bold]2.[/] Enter code: [bold green]{userCode}[/]")
                            .Header("Authenticate in your browser")
                            .BorderColor(Color.Blue));
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[dim]Waiting for authorization...[/]");
                        await Task.CompletedTask;
                    });
            }
            else
            {
                // Interactive flow (opens browser)
                success = await AnsiConsole.Status()
                    .StartAsync("Authenticating...", async ctx =>
                    {
                        return await _authService.AuthenticateInteractiveAsync(
                            clientId,
                            clientSecret,
                            scopes,
                            accountId);
                    });
            }

            if (!success)
            {
                AnsiConsole.MarkupLine("[red]Authentication failed.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]✓ Authentication successful![/]");
            AnsiConsole.WriteLine();
```

**Step 3: Verify it builds**

Run: `cd ~/Personal/calendar-mcp/src && dotnet build CalendarMcp.Cli/CalendarMcp.Cli.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/CalendarMcp.Cli/Commands/AddGoogleAccountCommand.cs
git commit -m "feat: add --device-code flag to add-google-account command"
```

---

## Task 6: Update flake.nix for HTTP Server

**Files:**
- Modify: `flake.nix`
- Create: `deps-http.json`

**Step 1: Generate deps-http.json**

Run:
```bash
cd ~/Personal/calendar-mcp/src
dotnet restore CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj
cd ..
# Use the update script pattern
```

Note: The deps file needs to be generated. For now, create a placeholder and we'll generate it properly.

**Step 2: Update flake.nix packages section**

Add the `http` package after `default` in the `packages` attribute:

```nix
          # HTTP Server with OAuth support
          http = pkgs.buildDotnetModule {
            pname = "calendar-mcp-http";
            inherit version;

            src = ./src;

            projectFile = "CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj";
            executables = [ "CalendarMcp.HttpServer" ];

            dotnet-sdk = dotnetSdk;
            dotnet-runtime = dotnetRuntime;

            nugetDeps = ./deps-http.json;
            runtimeDeps = [ pkgs.icu ];

            meta = with pkgs.lib; {
              description = "HTTP MCP server for unified email and calendar access";
              homepage = "https://github.com/Veraticus/calendar-mcp";
              license = licenses.mit;
              platforms = platforms.all;
            };
          };
```

**Step 3: Update NixOS module for HTTP transport**

Replace the entire `nixosModules.default` section with:

```nix
      nixosModules.default = { config, lib, pkgs, ... }:
        let
          cfg = config.services.calendar-mcp;
        in {
          options.services.calendar-mcp = {
            enable = lib.mkEnableOption "Calendar MCP server";

            package = lib.mkOption {
              type = lib.types.package;
              default = self.packages.${pkgs.system}.http;
              description = "The calendar-mcp package to use";
            };

            transport = lib.mkOption {
              type = lib.types.enum [ "stdio" "http" ];
              default = "http";
              description = "Transport mode (stdio or http)";
            };

            host = lib.mkOption {
              type = lib.types.str;
              default = "127.0.0.1";
              description = "HTTP server bind address";
            };

            port = lib.mkOption {
              type = lib.types.port;
              default = 8000;
              description = "HTTP server port";
            };

            dataDir = lib.mkOption {
              type = lib.types.path;
              default = "/var/lib/calendar-mcp";
              description = "Directory for calendar-mcp data";
            };

            user = lib.mkOption {
              type = lib.types.str;
              default = "calendar-mcp";
              description = "User to run calendar-mcp as";
            };

            group = lib.mkOption {
              type = lib.types.str;
              default = "calendar-mcp";
              description = "Group to run calendar-mcp as";
            };

            # OAuth configuration
            accessClientIdFile = lib.mkOption {
              type = lib.types.nullOr lib.types.path;
              default = null;
              description = "Path to file containing ACCESS_CLIENT_ID";
            };

            accessClientSecretFile = lib.mkOption {
              type = lib.types.nullOr lib.types.path;
              default = null;
              description = "Path to file containing ACCESS_CLIENT_SECRET";
            };

            accessConfigUrl = lib.mkOption {
              type = lib.types.nullOr lib.types.str;
              default = null;
              description = "OIDC discovery URL for Cloudflare Access";
            };
          };

          config = lib.mkIf cfg.enable {
            users.users.${cfg.user} = {
              isSystemUser = true;
              group = cfg.group;
              home = cfg.dataDir;
              createHome = true;
            };

            users.groups.${cfg.group} = {};

            systemd.services.calendar-mcp = {
              description = "Calendar MCP Server";
              wantedBy = [ "multi-user.target" ];
              after = [ "network.target" ];

              environment = {
                HOME = cfg.dataDir;
                XDG_DATA_HOME = "${cfg.dataDir}/.local/share";
                CALENDAR_MCP_CONFIG = "${cfg.dataDir}/.local/share/CalendarMcp";
                MCP_SERVER_HOST = cfg.host;
                MCP_SERVER_PORT = toString cfg.port;
              };

              serviceConfig = {
                Type = "simple";
                User = cfg.user;
                Group = cfg.group;
                Restart = "on-failure";
                RestartSec = 5;

                # Hardening
                NoNewPrivileges = true;
                ProtectSystem = "strict";
                ProtectHome = true;
                PrivateTmp = true;
                ReadWritePaths = [ cfg.dataDir ];
              } // lib.optionalAttrs (cfg.accessClientIdFile != null) {
                LoadCredential = [
                  "access-client-id:${cfg.accessClientIdFile}"
                  "access-client-secret:${cfg.accessClientSecretFile}"
                ];
              };

              script = let
                exe = if cfg.transport == "http"
                  then "${cfg.package}/bin/CalendarMcp.HttpServer"
                  else "${self.packages.${pkgs.system}.default}/bin/CalendarMcp.StdioServer";
              in ''
                ${lib.optionalString (cfg.accessClientIdFile != null) ''
                  export ACCESS_CLIENT_ID=$(cat $CREDENTIALS_DIRECTORY/access-client-id)
                  export ACCESS_CLIENT_SECRET=$(cat $CREDENTIALS_DIRECTORY/access-client-secret)
                  export ACCESS_CONFIG_URL="${cfg.accessConfigUrl}"
                ''}
                exec ${exe}
              '';
            };
          };
        };
```

**Step 4: Update update-nix-deps.sh**

Replace contents of `update-nix-deps.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

echo "Generating NuGet dependencies for Nix..."

# Ensure we're in a nix shell with nuget-to-json
if ! command -v nuget-to-json &> /dev/null; then
    echo "Error: nuget-to-json not found. Run 'nix develop' first."
    exit 1
fi

cd src

echo "Restoring packages..."
dotnet restore

echo "Generating deps-server.json (StdioServer)..."
nuget-to-json CalendarMcp.StdioServer > ../deps-server.json

echo "Generating deps-cli.json (CLI)..."
nuget-to-json CalendarMcp.Cli > ../deps-cli.json

echo "Generating deps-http.json (HttpServer)..."
nuget-to-json CalendarMcp.HttpServer > ../deps-http.json

cd ..
echo "Done! Generated deps-server.json, deps-cli.json, and deps-http.json"
```

**Step 5: Commit**

```bash
git add flake.nix update-nix-deps.sh
git commit -m "feat: add HTTP server package and NixOS module to flake"
```

---

## Task 7: Generate NuGet Dependencies

**Files:**
- Create: `deps-http.json`

**Step 1: Enter dev shell and generate deps**

Run:
```bash
cd ~/Personal/calendar-mcp
nix develop
./update-nix-deps.sh
```

Expected: Three deps files generated

**Step 2: Verify deps-http.json was created**

Run: `ls -la deps-*.json`
Expected: deps-server.json, deps-cli.json, deps-http.json all exist

**Step 3: Commit**

```bash
git add deps-http.json deps-server.json deps-cli.json
git commit -m "chore: regenerate NuGet dependency files"
```

---

## Task 8: Test HTTP Server Build

**Step 1: Build with Nix**

Run:
```bash
cd ~/Personal/calendar-mcp
nix build .#http
```

Expected: Build succeeds, result symlink created

**Step 2: Test dev mode startup**

Run:
```bash
MCP_SERVER_PORT=8001 ./result/bin/CalendarMcp.HttpServer
```

Expected: Server starts on port 8001, logs "OAuth authentication disabled - running in dev mode"

Press Ctrl+C to stop.

**Step 3: Test endpoint responds**

In another terminal:
```bash
curl -s http://localhost:8001/mcp | head -20
```

Expected: Some response (may be error without proper MCP client, but server responds)

---

## Task 9: Test Device Code Flow

**Step 1: Build CLI**

Run:
```bash
cd ~/Personal/calendar-mcp
nix build .#cli
```

**Step 2: Test help shows new flag**

Run:
```bash
./result/bin/CalendarMcp.Cli add-google-account --help
```

Expected: Output includes `--device-code` option description

---

## Task 10: Final Integration Test

**Step 1: Tag the release**

```bash
cd ~/Personal/calendar-mcp
git tag -a v0.2.0 -m "Add HTTP transport with OAuth and device code flow"
```

**Step 2: Push changes**

```bash
git push origin add-nix-flake
git push origin v0.2.0
```

---

## Summary of Changes

### New Files
- `src/CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj`
- `src/CalendarMcp.HttpServer/Program.cs`
- `deps-http.json`

### Modified Files
- `src/calendar-mcp.slnx` - Added HttpServer project
- `src/CalendarMcp.Core/Services/IGoogleAuthenticationService.cs` - Added device code method
- `src/CalendarMcp.Core/Providers/GoogleAuthenticationService.cs` - Implemented device code flow
- `src/CalendarMcp.Cli/Commands/AddGoogleAccountCommand.cs` - Added --device-code flag
- `flake.nix` - Added http package, updated NixOS module
- `update-nix-deps.sh` - Handle all three dep files

### Environment Variables (HTTP Server)
| Variable | Required | Description |
|----------|----------|-------------|
| `ACCESS_CLIENT_ID` | For auth | OAuth client ID from Cloudflare Access |
| `ACCESS_CLIENT_SECRET` | For auth | OAuth client secret |
| `ACCESS_CONFIG_URL` | For auth | OIDC discovery URL |
| `MCP_SERVER_HOST` | No | Bind address (default: `0.0.0.0`) |
| `MCP_SERVER_PORT` | No | Port (default: `8000`) |
| `CALENDAR_MCP_CONFIG` | No | Override config directory |
