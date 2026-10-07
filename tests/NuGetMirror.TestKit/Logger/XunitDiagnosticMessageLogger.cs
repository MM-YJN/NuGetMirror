using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Sdk;

namespace NuGetMirror.TestKit.Logger;

/// <summary>
/// An <see cref="ILogger"/> implementation that writes log messages through xUnit's current test context.
/// </summary>
/// <remarks>
/// This logger is designed for use in xUnit test projects to capture and forward log output to the test output stream.
/// It supports scope management and customizable log formatting via <see cref="XunitLoggerOptions"/>.
/// </remarks>
public class XunitDiagnosticMessageLogger(
    LoggerExternalScopeProvider scopeProvider,
    string? categoryName,
    XunitLoggerOptions? options
    ) : ILogger
{
    private readonly XunitLoggerOptions _options = options ?? new();

    /// <summary>
    /// Creates a new <see cref="ILogger"/> instance that writes log messages through xUnit's current test context.
    /// </summary>
    /// <returns>An <see cref="ILogger"/> instance for logging through xUnit's current test context.</returns>
    public static ILogger CreateLogger() => new XunitDiagnosticMessageLogger(new LoggerExternalScopeProvider(), "");

    /// <summary>
    /// Creates a new <see cref="ILogger{T}"/> instance that writes log messages through xUnit's current test context.
    /// </summary>
    /// <typeparam name="T">The category type for the logger.</typeparam>
    /// <returns>An <see cref="ILogger{T}"/> instance for logging through xUnit's current test context.</returns>
    public static ILogger<T> CreateLogger<T>() => new XunitDiagnosticMessageLogger<T>(new LoggerExternalScopeProvider());

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitDiagnosticMessageLogger"/> class with the specified scope provider and category name.
    /// </summary>
    /// <param name="scopeProvider">The provider for managing logging scopes.</param>
    /// <param name="categoryName">The category name for the logger.</param>
    public XunitDiagnosticMessageLogger(LoggerExternalScopeProvider scopeProvider, string? categoryName)
        : this(scopeProvider, categoryName, appendScope: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitDiagnosticMessageLogger"/> class with the specified scope provider, category name, and scope inclusion option.
    /// </summary>
    /// <param name="scopeProvider">The provider for managing logging scopes.</param>
    /// <param name="categoryName">The category name for the logger.</param>
    /// <param name="appendScope">A value indicating whether to include scopes in the log output.</param>
    public XunitDiagnosticMessageLogger(LoggerExternalScopeProvider scopeProvider, string? categoryName, bool appendScope)
        : this(scopeProvider, categoryName, options: new XunitLoggerOptions { IncludeScopes = appendScope })
    {
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopeProvider.Push(state);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        string message = formatter(state, exception);

        string formattedLog = LogFormatter.Format(scopeProvider, categoryName, logLevel, message, exception, _options);

        try
        {
            TestContext.Current.SendDiagnosticMessage(formattedLog);
        }
        catch
        {
            // This can happen when the test is not active
        }
    }
}
