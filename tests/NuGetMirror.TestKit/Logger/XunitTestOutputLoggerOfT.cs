using Microsoft.Extensions.Logging;

using Xunit;

namespace NuGetMirror.TestKit.Logger;

/// <inheritdoc />
public sealed class XunitTestOutputLogger<T>(ITestOutputHelper testOutputHelper, LoggerExternalScopeProvider scopeProvider)
    : XunitTestOutputLogger(testOutputHelper, scopeProvider, typeof(T).FullName), ILogger<T>;
