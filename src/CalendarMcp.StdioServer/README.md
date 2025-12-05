# CalendarMcp.StdioServer

A Model Context Protocol (MCP) server that exposes calendar operations via stdin/stdout transport.

## Overview

This console application provides a stdio-based MCP server for calendar operations. It can be used with MCP clients like Claude Desktop, VS Code with GitHub Copilot, or any other MCP-compatible client that supports stdio transport.

## Building

```bash
dotnet build
```

## Running

```bash
dotnet run --project src/CalendarMcp.StdioServer
```

Or after building:

```bash
dotnet src/CalendarMcp.StdioServer/bin/Debug/net9.0/CalendarMcp.StdioServer.dll
```

## Publishing

To create a standalone executable:

```bash
dotnet publish -c Release -o publish
```

## MCP Client Configuration

### VS Code with GitHub Copilot

Add to your VS Code `settings.json`:

```json
{
  "github.copilot.chat.mcp.servers": {
    "calendar-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/CalendarMcp.StdioServer"]
    }
  }
}
```

Or if using a published binary:

```json
{
  "github.copilot.chat.mcp.servers": {
    "calendar-mcp": {
      "type": "stdio",
      "command": "path/to/CalendarMcp.StdioServer.exe"
    }
  }
}
```

### Claude Desktop

Add to Claude Desktop configuration (`%APPDATA%\Claude\claude_desktop_config.json` on Windows):

```json
{
  "mcpServers": {
    "calendar-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "src/CalendarMcp.StdioServer"]
    }
  }
}
```

## Architecture

This server uses:
- **ModelContextProtocol.NET**: The MCP SDK for .NET
- **Stdio Transport**: Communication via standard input/output streams
- **Microsoft.Extensions.Hosting**: For application lifecycle management
- **Microsoft.Extensions.Logging**: For diagnostic logging (written to stderr)

## Logging

All diagnostic logs are written to stderr to avoid interfering with the MCP protocol messages on stdout. This allows proper separation of:
- **stdout**: MCP protocol JSON messages
- **stderr**: Application logs and diagnostics

## Development

### Adding Tools

To add calendar-specific tools, create a tool class and register it:

1. Create a tool class with `[McpServerToolType]` attribute
2. Add tool methods with `[McpServerTool]` attribute
3. Register in Program.cs: `.WithTools<YourToolClass>()`

See the ModelContextProtocol.NET documentation for more details.

## References

- [Model Context Protocol Specification](https://modelcontextprotocol.io/)
- [ModelContextProtocol.NET](https://github.com/MarimerLLC/ModelContextProtocol.NET)
- [MCP Stdio Transport](https://spec.modelcontextprotocol.io/specification/basic/transports/#stdio)
