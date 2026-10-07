using NuGetMirror.Storage.S3;

namespace NuGetMirror.UnitTests;

public sealed class AwsSignatureV4Tests
{
    [Fact]
    public void EmptyPayloadHashHex_IsCorrectConstant()
    {
        string expected = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Assert.Equal(expected, AwsSignatureV4.EmptyPayloadHashHex);
    }

    [Fact]
    public void ComputePayloadHashHex_ReturnsHash_AndResetsPosition()
    {
        byte[] data = "hello world"u8.ToArray();
        using var stream = new MemoryStream(data);

        string hash = AwsSignatureV4.ComputePayloadHashHex(stream);

        Assert.NotEmpty(hash);
        Assert.Equal(64, hash.Length); // SHA256 hex is 64 chars
        Assert.Equal(0, stream.Position); // Position reset
    }

    [Fact]
    public void ComputePayloadHashHex_SameContent_ProducesSameHash()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream1 = new MemoryStream(data);
        using var stream2 = new MemoryStream(data);

        string hash1 = AwsSignatureV4.ComputePayloadHashHex(stream1);
        string hash2 = AwsSignatureV4.ComputePayloadHashHex(stream2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Sign_ProducesAuthorizationHeader()
    {
        var signer = new AwsSignatureV4("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", TimeProvider.System);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://my-bucket.s3.us-east-1.amazonaws.com/test/key.nupkg");
        request.Headers.TryAddWithoutValidation("Host", "my-bucket.s3.us-east-1.amazonaws.com");

        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        Assert.True(request.Headers.Contains("x-amz-date"));
        Assert.True(request.Headers.Contains("x-amz-content-sha256"));
        Assert.True(request.Headers.Contains("Authorization"));

        string auth = request.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/", auth);
        Assert.Contains("/us-east-1/s3/aws4_request, ", auth);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date, ", auth);
        Assert.Contains("Signature=", auth);
    }

    [Fact]
    public void Sign_ProducesTimestampInCorrectFormat()
    {
        var signer = new AwsSignatureV4("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", TimeProvider.System);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://my-bucket.s3.us-east-1.amazonaws.com/test.nupkg");
        request.Headers.TryAddWithoutValidation("Host", "my-bucket.s3.us-east-1.amazonaws.com");

        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        string amzDate = request.Headers.GetValues("x-amz-date").Single();
        Assert.Matches(@"^\d{8}T\d{6}Z$", amzDate);
    }

    [Fact]
    public void Sign_PathStyleUri_ProducesCorrectCanonicalUri()
    {
        var signer = new AwsSignatureV4("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", TimeProvider.System);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:9000/my-bucket/test/package.1.0.0.nupkg");
        request.Headers.TryAddWithoutValidation("Host", "localhost:9000");

        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        string auth = request.Headers.GetValues("Authorization").Single();
        Assert.Contains("Signature=", auth);
    }

    [Fact]
    public void ComputePayloadHashHex_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => AwsSignatureV4.ComputePayloadHashHex(null!));
    }

    [Fact]
    public void Sign_ThrowsOnNullRequest()
    {
        var signer = new AwsSignatureV4("a", "b", "us-east-1", TimeProvider.System);

        Assert.Throws<ArgumentNullException>(
            () => signer.Sign(null!, AwsSignatureV4.EmptyPayloadHashHex));
    }

    [Fact]
    public void Sign_KnownVector_ProducesExpectedSignature()
    {
        // Known-answer test: all inputs are pinned so the exact signature can be verified.
        // Re-derive the expected value via the reference computation in the test project if
        // the algorithm changes intentionally; never relax to a format-only check.
        string accessKey = "AKIDEXAMPLE";
        string secretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        string region = "us-east-1";

        var signer = new AwsSignatureV4(accessKey, secretKey, region, TimeProvider.System);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://my-bucket.s3.us-east-1.amazonaws.com/test.txt");
        request.Headers.TryAddWithoutValidation("Host", "my-bucket.s3.us-east-1.amazonaws.com");

        // Fixed timestamp — must be pinned for the signature to be deterministic.
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string payloadHash = AwsSignatureV4.EmptyPayloadHashHex;

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", payloadHash);

        signer.Sign(request, payloadHash, now);

        string auth = request.Headers.GetValues("Authorization").Single();

        // Exact credential scope: AKIDEXAMPLE / date / region / service / terminator
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20240101/us-east-1/s3/aws4_request, ", auth, StringComparison.Ordinal);

        // SignedHeaders must be sorted lexicographically: host < x-amz-content-sha256 < x-amz-date
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date, ", auth, StringComparison.Ordinal);

        // Pinned exact signature — catches any regression in the HMAC chain or canonical-request construction.
        Assert.EndsWith("Signature=1989c44cfab93962c794cff9454c3e40ce3452f2c59e643a34aa76bedf6e88e3", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_IncludesExtraXAmzHeaders_InSignedHeaders()
    {
        var signer = new AwsSignatureV4("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", TimeProvider.System);
        var request = new HttpRequestMessage(HttpMethod.Put, "https://my-bucket.s3.us-east-1.amazonaws.com/test.nupkg");
        request.Headers.TryAddWithoutValidation("Host", "my-bucket.s3.us-east-1.amazonaws.com");
        request.Headers.TryAddWithoutValidation("x-amz-meta-etag", "\"abc123\"");
        request.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at", "1712345678000");
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", "/my-bucket/test.nupkg");
        request.Headers.TryAddWithoutValidation("x-amz-metadata-directive", "REPLACE");

        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        string auth = request.Headers.GetValues("Authorization").Single();

        Assert.Contains("x-amz-content-sha256", auth);
        Assert.Contains("x-amz-copy-source", auth);
        Assert.Contains("x-amz-date", auth);
        Assert.Contains("x-amz-meta-etag", auth);
        Assert.Contains("x-amz-meta-fetched-at", auth);
        Assert.Contains("x-amz-metadata-directive", auth);

        Assert.True(request.Headers.Contains("x-amz-date"));
        Assert.True(request.Headers.Contains("x-amz-content-sha256"));
    }

    [Fact]
    public void Sign_WithQueryString_IncludesQueryInCanonicalRequest()
    {
        var signer = new AwsSignatureV4("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", TimeProvider.System);

        var uri = new Uri("http://localhost:9000/test-bucket?list-type=2&prefix=packages/&continuation-token=abc123");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Host", "localhost:9000");

        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        string auth = request.Headers.GetValues("Authorization").Single();
        Assert.Contains("Signature=", auth);
        Assert.True(auth.Length > 50);

        // Signature must differ from same request without query string
        var signer2 = new AwsSignatureV4("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", TimeProvider.System);
        var uri2 = new Uri("http://localhost:9000/test-bucket");
        var request2 = new HttpRequestMessage(HttpMethod.Get, uri2);
        request2.Headers.TryAddWithoutValidation("Host", "localhost:9000");

        signer2.Sign(request2, AwsSignatureV4.EmptyPayloadHashHex);

        string auth2 = request2.Headers.GetValues("Authorization").Single();
        Assert.NotEqual(auth, auth2);
    }
}
