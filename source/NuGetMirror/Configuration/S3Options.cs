namespace NuGetMirror.Configuration;

public sealed class S3Options
{
    /// <summary>
    /// Name of the S3 bucket in which cached objects are stored.
    /// </summary>
    public string Bucket { get; set; } = "";

    /// <summary>
    /// AWS region of the bucket (e.g. <c>us-east-1</c>). Defaults to <c>us-east-1</c>.
    /// </summary>
    /// <remarks>
    /// Used to construct the default endpoint URL and to sign requests with
    /// AWS Signature V4. When <see cref="ServiceUrl"/> is set this value is still
    /// required for request signing.
    /// </remarks>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Optional custom S3 endpoint URL for S3-compatible services such as MinIO
    /// or LocalStack (e.g. <c>http://localhost:9000</c>).
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> or empty, the standard AWS endpoint
    /// (<c>https://{bucket}.s3.{region}.amazonaws.com</c> for virtual-hosted style,
    /// or <c>https://s3.{region}.amazonaws.com</c> for path style) is used.
    /// When set, the bucket name and <see cref="UsePathStyle"/> setting determine
    /// how the final request URL is constructed.
    /// </remarks>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// AWS access key ID used for signing requests with AWS Signature V4.
    /// </summary>
    public string AccessKey { get; set; } = "";

    /// <summary>
    /// AWS secret access key used for signing requests with AWS Signature V4.
    /// </summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// Optional prefix prepended to every object key stored in the bucket.
    /// </summary>
    /// <remarks>
    /// Use this to scope the mirror's objects to a sub-path within a shared bucket
    /// (e.g. <c>nuget-mirror/</c>). The trailing slash is stripped and re-added
    /// automatically, so both <c>prefix</c> and <c>prefix/</c> are equivalent.
    /// Defaults to empty string (no prefix).
    /// </remarks>
    public string KeyPrefix { get; set; } = "";

    /// <summary>
    /// Use path-style S3 URLs (<c>endpoint/bucket/key</c>) instead of
    /// virtual-hosted-style (<c>bucket.endpoint/key</c>).
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Path-style addressing is required for MinIO and some other S3-compatible
    /// services. AWS S3 itself supports virtual-hosted-style (the default) and may
    /// deprecate path-style access in the future.
    /// </remarks>
    public bool UsePathStyle { get; set; }
}
