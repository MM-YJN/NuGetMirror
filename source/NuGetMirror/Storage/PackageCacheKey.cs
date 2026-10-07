using System.Diagnostics.CodeAnalysis;

namespace NuGetMirror.Storage;

internal static class PackageCacheKey
{
    public static string? TryCreate(string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
        {
            return null;
        }

        ReadOnlySpan<char> span = remainingPath.AsSpan().Trim('/');

        if (IsPackageFile(span))
        {
            return PackageFileKey(span);
        }

        return null;
    }

    public static string RegistrationCacheKey(string flavor, string path)
    {
        ArgumentNullException.ThrowIfNull(flavor);
        ArgumentNullException.ThrowIfNull(path);

        return new RegistrationKeyState(flavor.AsSpan(), path.AsSpan().Trim('/')).ToString();
    }

    public static bool IsReadmePath([NotNullWhen(true)] string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
        {
            return false;
        }

        ReadOnlySpan<char> span = remainingPath.AsSpan().Trim('/');
        int lastSlash = span.LastIndexOf('/');

        if (lastSlash < 0)
        {
            return false;
        }

        ReadOnlySpan<char> file = span[(lastSlash + 1)..];

        if (!file.Equals("readme", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int secondLastSlash = span[..lastSlash].LastIndexOf('/');
        int thirdLastSlash = secondLastSlash >= 0 ? span[..secondLastSlash].LastIndexOf('/') : -1;

        return thirdLastSlash < 0;
    }

    public static string ReadmeCacheKey(string remainingPath)
    {
        ReadOnlySpan<char> span = remainingPath.AsSpan().Trim('/');
        int lastSlash = span.LastIndexOf('/');
        int secondLastSlash = span[..lastSlash].LastIndexOf('/');
        ReadOnlySpan<char> id = span[..secondLastSlash];
        ReadOnlySpan<char> version = span.Slice(secondLastSlash + 1, lastSlash - secondLastSlash - 1);
        ReadOnlySpan<char> file = span[(lastSlash + 1)..];

        return new ReadmeKeyState(id, version, file).ToString();
    }

    private static bool IsPackageFile(ReadOnlySpan<char> span)
    {
        string dotNupkg = ".nupkg";
        string dotNuspec = ".nuspec";

        if (span.EndsWith(dotNupkg, StringComparison.OrdinalIgnoreCase) ||
            span.EndsWith(dotNuspec, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static string PackageFileKey(ReadOnlySpan<char> span)
    {
        int lastSlash = span.LastIndexOf('/');

        if (lastSlash < 0)
        {
            return string.Create(span.Length, span,
                (buffer, value) => value.ToLowerInvariant(buffer));
        }

        int secondLastSlash = span[..lastSlash].LastIndexOf('/');

        if (secondLastSlash < 0)
        {
            return string.Create(span.Length, span,
                (buffer, value) => value.ToLowerInvariant(buffer));
        }

        ReadOnlySpan<char> id = span[..secondLastSlash];
        ReadOnlySpan<char> version = span.Slice(secondLastSlash + 1, lastSlash - secondLastSlash - 1);
        ReadOnlySpan<char> file = span[(lastSlash + 1)..];

        return new KeyState(id, version, file).ToString();
    }

    private readonly ref struct KeyState
    {
        private readonly ReadOnlySpan<char> _id;
        private readonly ReadOnlySpan<char> _version;
        private readonly ReadOnlySpan<char> _file;

        public KeyState(ReadOnlySpan<char> id, ReadOnlySpan<char> version, ReadOnlySpan<char> file)
        {
            _id = id;
            _version = version;
            _file = file;
        }

        public override string ToString()
            => string.Create(_id.Length + 1 + _version.Length + 1 + _file.Length, this,
                (buffer, value) =>
                {
                    ReadOnlySpan<char> id = value._id;
                    ReadOnlySpan<char> version = value._version;
                    ReadOnlySpan<char> file = value._file;
                    id.ToLowerInvariant(buffer[..id.Length]);
                    buffer[id.Length] = '/';
                    version.ToLowerInvariant(buffer[(id.Length + 1)..(id.Length + 1 + version.Length)]);
                    buffer[id.Length + 1 + version.Length] = '/';
                    file.ToLowerInvariant(buffer[(id.Length + 1 + version.Length + 1)..]);
                });
    }

    private readonly ref struct RegistrationKeyState
    {
        private readonly ReadOnlySpan<char> _flavor;
        private readonly ReadOnlySpan<char> _path;

        public RegistrationKeyState(ReadOnlySpan<char> flavor, ReadOnlySpan<char> path)
        {
            _flavor = flavor;
            _path = path;
        }

        public override string ToString()
            => string.Create("$registration/".Length + _flavor.Length + 1 + _path.Length, this,
                (buffer, value) =>
                {
                    ReadOnlySpan<char> flavor = value._flavor;
                    ReadOnlySpan<char> path = value._path;
                    "$registration/".CopyTo(buffer);
                    int offset = "$registration/".Length;
                    flavor.ToLowerInvariant(buffer[offset..(offset + flavor.Length)]);
                    offset += flavor.Length;
                    buffer[offset] = '/';
                    path.ToLowerInvariant(buffer[(offset + 1)..]);
                });
    }

    private readonly ref struct ReadmeKeyState
    {
        private readonly ReadOnlySpan<char> _id;
        private readonly ReadOnlySpan<char> _version;
        private readonly ReadOnlySpan<char> _file;

        public ReadmeKeyState(ReadOnlySpan<char> id, ReadOnlySpan<char> version, ReadOnlySpan<char> file)
        {
            _id = id;
            _version = version;
            _file = file;
        }

        public override string ToString()
            => string.Create("$readme/".Length + _id.Length + 1 + _version.Length + 1 + _file.Length, this,
                (buffer, value) =>
                {
                    ReadOnlySpan<char> id = value._id;
                    ReadOnlySpan<char> version = value._version;
                    ReadOnlySpan<char> file = value._file;
                    "$readme/".CopyTo(buffer);
                    int offset = "$readme/".Length;
                    id.ToLowerInvariant(buffer[offset..(offset + id.Length)]);
                    offset += id.Length;
                    buffer[offset] = '/';
                    version.ToLowerInvariant(buffer[(offset + 1)..(offset + 1 + version.Length)]);
                    offset += 1 + version.Length;
                    buffer[offset] = '/';
                    file.ToLowerInvariant(buffer[(offset + 1)..]);
                });
    }
}
