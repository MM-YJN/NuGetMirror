using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

internal sealed class MirrorOptionsBasePathValidator : IValidateOptions<MirrorOptions>
{
    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        string? value = options.BasePath;

        if (string.IsNullOrWhiteSpace(value))
        {
            return ValidateOptionsResult.Success;
        }

        // Strip the surrounding whitespace and slashes that NormalizeBasePath would remove,
        // then validate the inner segments. Trim whitespace first so that boundary spaces
        // (e.g. " /nuget/ ") don't prevent slash-trimming from reaching the actual slashes.
        string inner = value.Trim().Trim('/');

        if (string.IsNullOrEmpty(inner))
        {
            // "/" or all slashes — normalizes to empty, which is fine.
            return ValidateOptionsResult.Success;
        }

        if (inner.Contains("..", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath must not contain path-traversal sequences ('..').");
        }

        if (inner.Contains("//", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath must not contain consecutive slashes ('//').");
        }

        if (inner.Contains('?') || inner.Contains('#'))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath must not contain a query string ('?') or fragment ('#').");
        }

        if (inner.Contains(':'))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath must not contain a colon (':'); use a plain path without scheme.");
        }

        if (inner.Contains('{') || inner.Contains('}'))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath must not contain route-parameter braces ('{' or '}'); use a plain literal path.");
        }

        if (inner.Contains('*'))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:BasePath must not contain a wildcard ('*'); use a plain literal path.");
        }

        // Disallow whitespace within the path itself (after trim).
        foreach (char c in inner)
        {
            if (char.IsWhiteSpace(c))
            {
                return ValidateOptionsResult.Fail(
                    "Mirror:BasePath must not contain whitespace.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
