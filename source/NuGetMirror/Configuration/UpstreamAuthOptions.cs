namespace NuGetMirror.Configuration;

public sealed class UpstreamAuthOptions
{
    /// <summary>
    /// Authentication scheme to use when sending requests to the upstream server.
    /// </summary>
    /// <remarks>
    /// Supported values:
    /// <list type="bullet">
    /// <item><term><c>Bearer</c></term>
    /// <description>Sends <c>Authorization: Bearer {Token}</c>.</description></item>
    /// <item><term><c>Basic</c></term>
    /// <description>Sends <c>Authorization: Basic {Token}</c>. The <see cref="Token"/>
    /// value should be the Base64-encoded <c>username:password</c> string.</description></item>
    /// <item><term><c>Header</c></term>
    /// <description>Sends <c>{HeaderName}: {Token}</c> as a custom header.
    /// Both <see cref="HeaderName"/> and <see cref="Token"/> must be set.</description></item>
    /// </list>
    /// </remarks>
    public string? Scheme { get; set; }

    /// <summary>
    /// The credential value sent to the upstream server.
    /// </summary>
    /// <remarks>
    /// For <c>Bearer</c> scheme this is the raw bearer token. For <c>Basic</c> scheme
    /// this is the Base64-encoded <c>username:password</c> string. For <c>Header</c>
    /// scheme this is the value of the custom header specified by <see cref="HeaderName"/>.
    /// </remarks>
    public string? Token { get; set; }

    /// <summary>
    /// The custom HTTP header name used when <see cref="Scheme"/> is <c>Header</c>.
    /// </summary>
    /// <remarks>
    /// Ignored for <c>Bearer</c> and <c>Basic</c> schemes. Must be set together
    /// with <see cref="Token"/> when using the <c>Header</c> scheme.
    /// </remarks>
    public string? HeaderName { get; set; }
}
