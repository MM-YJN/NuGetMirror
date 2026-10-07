using Microsoft.Extensions.Logging;

namespace NuGetMirror.TestKit.Logger;

/// <inheritdoc />
public sealed class XunitDiagnosticMessageLogger<T>(LoggerExternalScopeProvider scopeProvider)
    : XunitDiagnosticMessageLogger(scopeProvider, typeof(T).FullName), ILogger<T>;
