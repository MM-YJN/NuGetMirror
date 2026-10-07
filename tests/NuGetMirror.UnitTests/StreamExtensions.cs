namespace NuGetMirror.UnitTests;

internal static class StreamExtensions
{
    internal static async Task<byte[]> ReadByteArrayAsync(this Stream _, Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
