using Microsoft.Extensions.Logging;

using Xunit.Sdk;

namespace NuGetMirror.TestKit.Logger;

/// <summary>
/// Provides an <see cref="ILoggerProvider"/> implementation that creates loggers writing diagnostic messages through xUnit's current test context.
/// </summary>
/// <remarks>
/// This provider is intended for use in xUnit test projects to capture and forward log output to the test output stream.
/// It supports scope management and customizable log formatting via <see cref="XunitLoggerOptions"/>.
/// </remarks>
public sealed class XunitDiagnosticMessageLoggerProvider(
    XunitLoggerOptions? options
    ) : ILoggerProvider
{
    private readonly XunitLoggerOptions _options = options ?? new XunitLoggerOptions();
    private readonly LoggerExternalScopeProvider _scopeProvider = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitDiagnosticMessageLoggerProvider"/> class.
    /// Uses default <see cref="XunitLoggerOptions"/>.
    /// </summary>
    public XunitDiagnosticMessageLoggerProvider()
        : this(options: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitDiagnosticMessageLoggerProvider"/> class with a value indicating whether to include scopes in the log output.
    /// </summary>
    /// <param name="appendScope">If <c>true</c>, scopes will be included in the log output.</param>
    public XunitDiagnosticMessageLoggerProvider(bool appendScope)
        : this(new XunitLoggerOptions { IncludeScopes = appendScope })
    {
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new XunitDiagnosticMessageLogger(_scopeProvider, categoryName, _options);

    /// <inheritdoc />
    public void Dispose() { }
}
