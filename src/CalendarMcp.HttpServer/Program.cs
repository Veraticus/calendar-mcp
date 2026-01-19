using CalendarMcp.Core.Configuration;
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
                Log.Warning("OAuth authentication disabled - running in DEVELOPMENT MODE (no authentication)");
            }

            // Add MCP server with HTTP transport
            builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithCalendarMcpTools();

            var app = builder.Build();

            // Health check endpoint (always accessible, no auth required)
            app.MapGet("/health", () => Results.Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
            }));

            app.MapGet("/healthz", () => Results.Ok("ok")); // Simple liveness probe

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

            // Set up graceful shutdown
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStarted.Register(() =>
                Log.Information("Calendar MCP HTTP server started on {Url}", url));
            lifetime.ApplicationStopping.Register(() =>
                Log.Information("Calendar MCP HTTP server is shutting down..."));
            lifetime.ApplicationStopped.Register(() =>
                Log.Information("Calendar MCP HTTP server stopped"));

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
