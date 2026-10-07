using System.Diagnostics.Metrics;

namespace NuGetMirror.TestKit;

/// <summary>
/// A <see cref="MeterListener"/> wrapper that captures metric measurements for a named
/// <see cref="Meter"/>. Use the instance API (<see cref="Find"/>) for async flows, or the
/// static <see cref="Capture(string, Action)"/> / <see cref="CaptureObservable"/> helpers
/// for synchronous one-shot recording.
/// </summary>
public sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];

    public MetricsCapture(string meterName)
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            _measurements.Add(new Measurement(instrument.Name, measurement, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            _measurements.Add(new Measurement(instrument.Name, measurement, tags.ToArray())));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Measurements => _measurements;

    public Measurement? Find(string instrument, string? tagKey = null, string? tagValue = null)
    {
        foreach (Measurement m in _measurements)
        {
            if (m.Instrument != instrument)
            {
                continue;
            }

            if (tagKey is not null && m.GetTag(tagKey) != tagValue)
            {
                continue;
            }

            return m;
        }

        return null;
    }

    /// <summary>
    /// Starts a listener, executes <paramref name="record"/>, and returns all captured
    /// measurements. Convenient for testing individual metric methods synchronously.
    /// </summary>
    public static List<Measurement> Capture(string meterName, Action record)
    {
        using var capture = new MetricsCapture(meterName);
        record();
        return [.. capture._measurements];
    }

    /// <summary>
    /// Starts a listener, records observable instruments once, and returns all captured
    /// measurements. Use this for observable gauges that are updated outside of a
    /// recording callback.
    /// </summary>
    public static List<Measurement> CaptureObservable(string meterName)
    {
        using var capture = new MetricsCapture(meterName);
        capture._listener.RecordObservableInstruments();
        return [.. capture._measurements];
    }

    public void Dispose() => _listener.Dispose();
}
