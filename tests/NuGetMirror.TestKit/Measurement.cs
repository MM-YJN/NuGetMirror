namespace NuGetMirror.TestKit;

/// <summary>
/// A single recorded metric measurement captured by <see cref="MetricsCapture"/>.
/// </summary>
public sealed class Measurement(string instrument, double value, KeyValuePair<string, object?>[] tags)
{
    public string Instrument { get; } = instrument;

    public double Value { get; } = value;

    public KeyValuePair<string, object?>[] Tags { get; } = tags;

    public string? GetTag(string name)
    {
        foreach (KeyValuePair<string, object?> tag in Tags)
        {
            if (tag.Key == name)
            {
                return tag.Value as string;
            }
        }

        return null;
    }
}
