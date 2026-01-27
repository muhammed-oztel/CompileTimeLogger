# CompileTimeLogger

A Roslyn analyzer that detects `ILogger.Log*` method calls and provides code fixes to convert them to compile-time generated logging using the `[LoggerMessage]` attribute for better performance.

[![Build and Publish](https://github.com/YOUR_USERNAME/CompileTimeLogger/actions/workflows/build.yml/badge.svg)](https://github.com/YOUR_USERNAME/CompileTimeLogger/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/CompileTimeLogger.svg)](https://www.nuget.org/packages/CompileTimeLogger/)

## Why Use Compile-Time Logging?

The `[LoggerMessage]` attribute (introduced in .NET 6) enables source generation for high-performance logging:

- **Zero allocations** - No boxing of value types
- **No parsing at runtime** - Message templates are parsed at compile time
- **Strongly typed** - Compile-time validation of log message parameters
- **Better performance** - Up to 6x faster than traditional logging extensions

## Installation

```bash
dotnet add package CompileTimeLogger
```

Or via Package Manager:

```powershell
Install-Package CompileTimeLogger
```

## Usage

The analyzer detects calls like:

```csharp
_logger.LogInformation("User {UserId} logged in", userId);
```

And offers two code fixes:

### Fix 1: Convert to Instance Method

```csharp
// Before
_logger.LogInformation("User {UserId} logged in", userId);

// After
LogUserLoggedIn(userId);

[LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} logged in")]
private partial void LogUserLoggedIn(string userId);
```

### Fix 2: Convert to Private Log Class

```csharp
// Before
_logger.LogInformation("User {UserId} logged in", userId);

// After
Log.UserLoggedIn(_logger, userId);

private static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} logged in")]
    public static partial void UserLoggedIn(ILogger logger, string userId);
}
```

## Supported Log Methods

- `LogTrace`
- `LogDebug`
- `LogInformation`
- `LogWarning`
- `LogError`
- `LogCritical`

## Features

- Works with both `ILogger` and `ILogger<T>`
- Handles exceptions in log calls (e.g., `_logger.LogError(ex, "message")`)
- Automatically extracts message template placeholders
- Generates properly named methods from message templates
- Makes containing class `partial` automatically

## Requirements

- .NET 6.0 or later (for `[LoggerMessage]` attribute support)
- C# 9.0 or later

## Diagnostic ID

| ID | Description |
|----|-------------|
| CTL001 | ILogger.Log* call can be converted to compile-time generated logging |

## License

MIT License - see [LICENSE.txt](LICENSE.txt)
