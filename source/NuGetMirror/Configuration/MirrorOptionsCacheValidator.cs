using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

internal sealed class MirrorOptionsCacheValidator : IValidateOptions<MirrorOptions>
{
    private static readonly string[] s_allowedBackends = ["FileSystem", "S3"];

    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        var errors = new List<string>();

        string backend = options.Cache.Backend;
        if (string.IsNullOrEmpty(backend)
            || !s_allowedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Mirror:Cache:Backend value '{backend}' must be one of: {string.Join(", ", s_allowedBackends)}.");
        }

        string? dir = options.Cache.FileSystem.Directory?.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            errors.Add("Mirror:Cache:FileSystem:Directory must be a non-empty path.");
        }
        else
        {
            string[] segments = dir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (Array.Exists(segments, s => s == ".."))
            {
                errors.Add($"Mirror:Cache:FileSystem:Directory value '{dir}' must not contain '..'.");
            }
        }

        CheckNonNegative("Mirror:Cache:Eviction:Interval", options.Cache.Eviction.Interval, errors);
        CheckNonNegative("Mirror:Cache:SizeReporting:Interval", options.Cache.SizeReporting.Interval, errors);
        CheckNonNegative("Mirror:Cache:NegativeCache:Ttl", options.Cache.NegativeCache.Ttl, errors);
        CheckNonNegative("Mirror:Cache:Vulnerability:CacheTtl", options.Cache.Vulnerability.CacheTtl, errors);
        CheckNonNegative("Mirror:Cache:Readme:CacheTtl", options.Cache.Readme.CacheTtl, errors);
        CheckNonNegative("Mirror:Cache:RepositorySignatures:CacheTtl", options.Cache.RepositorySignatures.CacheTtl, errors);
        CheckNonNegative("Mirror:Cache:Registration:CacheTtl", options.Cache.Registration.CacheTtl, errors);

        if (options.Cache.Eviction.MaxSizeBytes is long maxSize && maxSize < 1)
        {
            errors.Add($"Mirror:Cache:Eviction:MaxSizeBytes must be >= 1 when set. Current value: {maxSize}.");
        }

        if (options.Cache.Eviction.MaxAge is TimeSpan maxAge && maxAge < TimeSpan.Zero)
        {
            errors.Add($"Mirror:Cache:Eviction:MaxAge must be >= 0 when set. Current value: {maxAge}.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckNonNegative(string key, TimeSpan value, List<string> errors)
    {
        if (value < TimeSpan.Zero)
        {
            errors.Add($"{key} must be a non-negative TimeSpan. Current value: {value}.");
        }
    }
}
