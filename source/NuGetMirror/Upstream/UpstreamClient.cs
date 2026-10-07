using System.Diagnostics;
using System.Net.Http.Headers;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;

namespace NuGetMirror.Upstream;

internal sealed partial class UpstreamClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MirrorOptions> options,
    ILogger<UpstreamClient> logger,
    MirrorMetrics metrics)
{
    public const string BufferedClientName = "upstream-buffered";
    public const string StreamClientName = "upstream-stream";

    /// <summary>Returns headers without buffering content. The stream flag selects the request policy and metric labels.</summary>
    public async Task<HttpResponseMessage> GetAsync(Uri upstreamUri, bool stream, CancellationToken ct, string? ifNoneMatch = null)
    {
        string mode = stream ? "stream" : "buffered";
        using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUri);
        ApplyAuth(request);

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        HttpClient http = httpClientFactory.CreateClient(stream ? StreamClientName : BufferedClientName);
        long started = Stopwatch.GetTimestamp();

        try
        {
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            string outcome = response.StatusCode == System.Net.HttpStatusCode.NotModified
                ? "not_modified"
                : response.IsSuccessStatusCode ? "success" : "nonsuccess";
            metrics.RecordUpstreamRequest((int)response.StatusCode, mode, outcome, elapsed);

            if (!response.IsSuccessStatusCode)
            {
                LogUpstreamNonSuccess(upstreamUri, (int)response.StatusCode);
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            metrics.RecordUpstreamRequest(0, mode, "error", elapsed);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upstream request returned {StatusCode}: {Uri}")]
    private partial void LogUpstreamNonSuccess(Uri uri, int statusCode);

    private void ApplyAuth(HttpRequestMessage request)
    {
        UpstreamAuthOptions? auth = options.Value.Upstream.Auth;
        if (auth is null)
        {
            return;
        }

        switch (auth.Scheme)
        {
            case "Bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
                break;
            case "Basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth.Token);
                break;
            case "Header":
                if (auth.HeaderName is not null && auth.Token is not null)
                {
                    request.Headers.TryAddWithoutValidation(auth.HeaderName, auth.Token);
                }

                break;
        }
    }
}
