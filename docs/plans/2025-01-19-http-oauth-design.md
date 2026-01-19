# HTTP Server with OAuth Support

Design for adding HTTP transport and authentication to calendar-mcp.

## Goals

1. **HTTP transport**: Allow Claude Desktop/iOS to connect remotely (like redlib-mcp)
2. **Cloudflare Access OAuth**: Secure the MCP endpoint for remote access
3. **Dev mode**: Local testing without authentication
4. **Device code flow for Google**: Enable headless account setup without port forwarding

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    CalendarMcp.HttpServer                    │
│  (ASP.NET Core)                                             │
│                                                              │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐  │
│  │ OIDC Auth    │───▶│ MCP HTTP     │───▶│ Core Tools   │  │
│  │ Middleware   │    │ Transport    │    │ (existing)   │  │
│  └──────────────┘    └──────────────┘    └──────────────┘  │
│         │                                                    │
│         ▼                                                    │
│  ┌──────────────┐                                           │
│  │ Dev Mode     │ (bypass auth when ACCESS_* not set)       │
│  │ Bypass       │                                           │
│  └──────────────┘                                           │
└─────────────────────────────────────────────────────────────┘
```

## Components

### 1. New Project: CalendarMcp.HttpServer

ASP.NET Core web application replacing the stdio transport.

**Dependencies:**
- `ModelContextProtocol.AspNetCore` - HTTP transport for MCP
- `Microsoft.AspNetCore.Authentication.OpenIdConnect` - OIDC auth

**Program.cs structure:**
```csharp
var builder = WebApplication.CreateBuilder(args);

// Load config same as StdioServer
builder.Configuration.AddJsonFile(ConfigurationPaths.GetConfigFilePath(), optional: true);
builder.Configuration.AddEnvironmentVariables("CALENDAR_MCP_");

// Add core services
builder.Services.AddCalendarMcpCore();

// Configure auth (if ACCESS_* env vars present)
var accessClientId = Environment.GetEnvironmentVariable("ACCESS_CLIENT_ID");
var accessClientSecret = Environment.GetEnvironmentVariable("ACCESS_CLIENT_SECRET");

if (!string.IsNullOrEmpty(accessClientId) && !string.IsNullOrEmpty(accessClientSecret))
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddOpenIdConnect(options =>
        {
            options.MetadataAddress = Environment.GetEnvironmentVariable("ACCESS_CONFIG_URL");
            options.ClientId = accessClientId;
            options.ClientSecret = accessClientSecret;
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Add("openid");
            options.Scope.Add("email");
        });

    builder.Services.AddAuthorization();
}

// Add MCP with HTTP transport
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

// Auth middleware (only if configured)
if (!string.IsNullOrEmpty(accessClientId))
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcp().RequireAuthorization();
}
else
{
    // Dev mode - no auth
    app.MapMcp();
}

var host = Environment.GetEnvironmentVariable("MCP_SERVER_HOST") ?? "0.0.0.0";
var port = int.Parse(Environment.GetEnvironmentVariable("MCP_SERVER_PORT") ?? "8000");
app.Run($"http://{host}:{port}");
```

### 2. Environment Variables

Matching redlib-mcp pattern:

| Variable | Required | Description |
|----------|----------|-------------|
| `ACCESS_CLIENT_ID` | For auth | OAuth client ID from Cloudflare Access |
| `ACCESS_CLIENT_SECRET` | For auth | OAuth client secret |
| `ACCESS_CONFIG_URL` | For auth | OIDC discovery URL |
| `MCP_SERVER_HOST` | No | Bind address (default: `0.0.0.0`) |
| `MCP_SERVER_PORT` | No | Port (default: `8000`) |
| `CALENDAR_MCP_CONFIG` | No | Override config directory |

**Dev mode**: If `ACCESS_CLIENT_ID` is not set, auth is bypassed entirely.

### 3. Device Code Flow for Google

Add to `IGoogleAuthenticationService`:

```csharp
Task<bool> AuthenticateWithDeviceCodeAsync(
    string clientId,
    string clientSecret,
    string[] scopes,
    string accountId,
    Func<string, string, Task> deviceCodeCallback, // (verificationUrl, userCode) => display to user
    CancellationToken cancellationToken = default);
```

Implementation uses Google's OAuth 2.0 device flow:
1. POST to `https://oauth2.googleapis.com/device/code`
2. Display verification URL and user code to user
3. Poll `https://oauth2.googleapis.com/token` until authorized
4. Store refresh token in same location as interactive flow

**CLI command**: `add-google-device` (or `--device-code` flag on `add-google`)

### 4. Updated flake.nix

Add new package output:

```nix
packages = {
  default = ... # existing StdioServer

  http = pkgs.buildDotnetModule {
    pname = "calendar-mcp-http";
    inherit version;
    src = ./src;
    projectFile = "CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj";
    executables = [ "CalendarMcp.HttpServer" ];
    dotnet-sdk = dotnetSdk;
    dotnet-runtime = dotnetRuntime;
    nugetDeps = ./deps-http.json;
    # ...
  };

  cli = ... # existing
};
```

### 5. Updated NixOS Module

Support both stdio and HTTP modes:

```nix
options.services.calendar-mcp = {
  enable = lib.mkEnableOption "Calendar MCP server";

  transport = lib.mkOption {
    type = lib.types.enum [ "stdio" "http" ];
    default = "http";
    description = "Transport mode";
  };

  port = lib.mkOption {
    type = lib.types.port;
    default = 8000;
    description = "HTTP server port";
  };

  host = lib.mkOption {
    type = lib.types.str;
    default = "127.0.0.1";
    description = "HTTP server bind address";
  };

  # Cloudflare Access OAuth
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
```

## File Changes Summary

### New Files

1. `src/CalendarMcp.HttpServer/CalendarMcp.HttpServer.csproj`
2. `src/CalendarMcp.HttpServer/Program.cs`
3. `deps-http.json` (NuGet dependencies)

### Modified Files

1. `src/CalendarMcp.Core/Services/IGoogleAuthenticationService.cs` - Add device code method
2. `src/CalendarMcp.Core/Providers/GoogleAuthenticationService.cs` - Implement device code flow
3. `src/CalendarMcp.Cli/Program.cs` - Register new command
4. `src/CalendarMcp.Cli/Commands/AddGoogleAccountCommand.cs` - Add `--device-code` flag
5. `flake.nix` - Add http package, update module
6. `update-nix-deps.sh` - Handle multiple dep files

## Testing Plan

### Local Dev Mode

```bash
# Build and run without auth
nix build .#http
MCP_SERVER_PORT=8000 ./result/bin/CalendarMcp.HttpServer

# Test with curl
curl http://localhost:8000/mcp/v1/tools
```

### With Cloudflare Access

```bash
# Set up Access application in Cloudflare dashboard
# Get client ID/secret for the application

ACCESS_CLIENT_ID=xxx \
ACCESS_CLIENT_SECRET=yyy \
ACCESS_CONFIG_URL=https://team.cloudflareaccess.com/cdn-cgi/access/sso/oidc/appid/.well-known/openid-configuration \
MCP_SERVER_PORT=8000 \
./result/bin/CalendarMcp.HttpServer
```

### Device Code Flow

```bash
# Run CLI with device code flag
CalendarMcp.Cli add-google --device-code

# Output:
# Visit https://www.google.com/device and enter code: XXXX-YYYY
# Waiting for authorization...
# ✓ Authentication successful!
```

## Implementation Order

1. **Create CalendarMcp.HttpServer project** - Basic HTTP transport, no auth
2. **Add dev mode testing** - Verify tools work over HTTP
3. **Add OIDC authentication** - Cloudflare Access integration
4. **Implement Google device code flow** - Headless account setup
5. **Update flake.nix** - New package and deps
6. **Update NixOS module** - HTTP transport support
7. **Deploy and test on ultraviolet** - End-to-end verification

## Open Questions

1. **Session state**: The MCP C# SDK supports stateless mode for load balancing. Do we need this, or is single-instance fine?
   - **Recommendation**: Single instance is fine for personal use

2. **Token refresh in HTTP mode**: The stdio server runs continuously. HTTP mode might idle. Need to ensure token refresh still works.
   - **Recommendation**: Tokens refresh on-demand when tools are called, should be fine

3. **Multiple simultaneous sessions**: Should we support multiple Claude clients connected at once?
   - **Recommendation**: Yes, the HTTP transport handles this naturally
