using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Configuration;

/// <summary>
/// Extension methods for configuring Calendar MCP services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Calendar MCP core services to the dependency injection container
    /// </summary>
    public static IServiceCollection AddCalendarMcpCore(this IServiceCollection services)
    {
        // Register authentication services
        services.AddSingleton<IM365AuthenticationService, M365AuthenticationService>();
        services.AddSingleton<IGoogleAuthenticationService, GoogleAuthenticationService>();

        // Register provider services
        services.AddSingleton<IM365ProviderService, M365ProviderService>();
        services.AddSingleton<IGoogleProviderService, GoogleProviderService>();
        services.AddSingleton<IOutlookComProviderService, OutlookComProviderService>();
        services.AddSingleton<IProviderServiceFactory, ProviderServiceFactory>();

        // Register account registry
        services.AddSingleton<IAccountRegistry, AccountRegistry>();

        // Register MCP tools (method-based pattern - just register the classes)
        services.AddSingleton<ListAccountsTool>();
        services.AddSingleton<GetEmailsTool>();
        services.AddSingleton<SearchEmailsTool>();
        services.AddSingleton<ListCalendarsTool>();
        services.AddSingleton<GetCalendarEventsTool>();
        services.AddSingleton<SendEmailTool>();
        services.AddSingleton<CreateEventTool>();

        return services;
    }

    /// <summary>
    /// Registers all Calendar MCP tools with the MCP server builder.
    /// Use this to ensure consistent tool registration across all server implementations.
    /// </summary>
    public static IMcpServerBuilder WithCalendarMcpTools(this IMcpServerBuilder builder)
    {
        return builder
            .WithTools<ListAccountsTool>()
            .WithTools<GetEmailsTool>()
            .WithTools<SearchEmailsTool>()
            .WithTools<GetContextualEmailSummaryTool>()
            .WithTools<ListCalendarsTool>()
            .WithTools<GetCalendarEventsTool>()
            .WithTools<SendEmailTool>()
            .WithTools<CreateEventTool>();
    }
}
