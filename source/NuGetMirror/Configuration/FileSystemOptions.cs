using System.ComponentModel.DataAnnotations;

namespace NuGetMirror.Configuration;

public sealed class FileSystemOptions
{
    public const string DefaultDirectory = "mirror-cache";

    /// <summary>
    /// Root directory where cached files are stored when using the
    /// <c>FileSystem</c> cache backend. Defaults to <c>mirror-cache</c>.
    /// </summary>
    /// <remarks>
    /// Relative paths are resolved from the current working directory of the
    /// process. The directory is created automatically at startup if it does
    /// not exist. Each cached resource is stored as a separate file using a
    /// path derived from its cache key.
    /// </remarks>
    [Required]
    public string Directory { get; set; } = DefaultDirectory;
}
