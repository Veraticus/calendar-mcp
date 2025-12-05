# .NET Development Guidelines

## .NET Version

- Use .NET 10 for all new projects unless there is a specific requirement to use an older version

## Async Programming

- Prefer `async Task` over `void` for methods that perform any asynchronous operations
- Only use `async void` for event handlers where required by the event signature
- Always await async operations rather than using `.Result` or `.Wait()` to avoid deadlocks
- Always use async/await for IO operations (file, network, database)
- Use cancellation tokens for async methods where possible to support graceful cancellation

## Dependency Injection

- Prefer dependency injection for managing service lifetimes and dependencies
- Prefer dependency injection over singleton patterns for better testability and maintainability
- Register services in the DI container rather than using `new` keyword for service instantiation
- Use constructor injection as the primary pattern for receiving dependencies
- Follow proper service lifetime patterns (Singleton, Scoped, Transient)

## Logging and Telemetry

- Use `ILogger<T>` for structured logging throughout the application
- Leverage OpenTelemetry for distributed tracing and observability
- Include relevant context in log messages using structured logging parameters
- Use appropriate log levels (Trace, Debug, Information, Warning, Error, Critical)
- Instrument critical paths with OpenTelemetry spans for performance monitoring

## Console Applications

- Use Spectre.Console for building rich console applications with enhanced UI components
- Leverage Spectre.Console's features like tables, progress bars, and prompts for better user experience
- Follow best practices for console application design, including clear input/output handling and error reporting

## Error Handling

- Use try-catch blocks to handle exceptions gracefully
- Log exceptions with sufficient context for troubleshooting
- Avoid catching general exceptions; catch specific exception types where possible
- Use custom exception types for domain-specific errors
- Ensure proper resource cleanup in finally blocks or by using `using` statements

## Configuration Management

- Use `IConfiguration` for managing application settings
- Never hardcode configuration values; use environment variables or dotnet user secrets for sensitive data
- Structure configuration files (appsettings.json) for clarity and maintainability
- Use options pattern (`IOptions<T>`) for strongly typed configuration access
- Validate configuration settings at application startup
