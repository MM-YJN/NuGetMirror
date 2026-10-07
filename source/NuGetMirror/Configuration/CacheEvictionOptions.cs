using System.ComponentModel.DataAnnotations;

namespace NuGetMirror.Configuration;

public sealed class CacheEvictionOptions
{
    /// <summary>
    /// Whether the background cache eviction sweep is enabled. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/> the eviction service runs periodically (see <see cref="Interval"/>)
    /// and removes cached entries according to the configured <see cref="MaxAge"/> and
    /// <see cref="MaxSizeBytes"/> policies. Requires <c>Mirror:Cache:Enabled</c> to also be
    /// <see langword="true"/>. Set to <see langword="false"/> to disable eviction while
    /// optionally still reporting cache size via <c>Mirror:Cache:SizeReporting</c>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The ordering strategy used when selecting entries to evict. Defaults to <see cref="EvictionStrategy.Oldest"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><term><see cref="EvictionStrategy.Oldest"/></term>
    /// <description>Evicts entries by their original creation time (ascending). Cache hits
    /// do <em>not</em> update the timestamp, so heavily-requested entries are treated the
    /// same as idle ones.</description></item>
    /// <item><term><see cref="EvictionStrategy.Lru"/></term>
    /// <description>Evicts entries by last-access time. On a cache hit the entry's timestamp
    /// is refreshed (<c>LastWriteTimeUtc</c> on the file system; a self-copy on S3), so
    /// frequently-accessed entries are retained longer.</description></item>
    /// </list>
    /// </remarks>
    public EvictionStrategy Strategy { get; set; } = EvictionStrategy.Oldest;

    /// <summary>
    /// Optional maximum total size of the cache in bytes.
    /// </summary>
    /// <remarks>
    /// When the total size of all cached entries exceeds this limit, the eviction sweep
    /// removes the oldest (or least-recently-used) entries until the total drops to
    /// <c>MaxSizeBytes × <see cref="TargetUtilization"/></c>. When <see langword="null"/>
    /// (the default) no size limit is enforced.
    /// </remarks>
    public long? MaxSizeBytes { get; set; }

    /// <summary>
    /// Optional maximum age for cached entries.
    /// </summary>
    /// <remarks>
    /// During each sweep, any entry whose last-modified timestamp is older than this
    /// duration is deleted unconditionally, regardless of total cache size. The entry
    /// will be re-fetched from upstream on the next request. When <see langword="null"/>
    /// (the default) no age limit is enforced.
    /// </remarks>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// How often the eviction sweep runs. Defaults to 15 minutes.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Fraction of <see cref="MaxSizeBytes"/> to target after a size-triggered eviction sweep.
    /// Defaults to <c>0.9</c> (90%).
    /// </summary>
    /// <remarks>
    /// For example, with <c>MaxSizeBytes = 10 GiB</c> and <c>TargetUtilization = 0.9</c>,
    /// the sweep will evict entries until the total cache size is at most 9 GiB. Must be
    /// in the range [0.0, 1.0].
    /// </remarks>
    [Range(0.0, 1.0)]
    public double TargetUtilization { get; set; } = 0.9;
}
