using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KSC.MeetingManagement.Application.Telemetry;

namespace KSC.MeetingManagement.Api.Observability;

/// <summary>
/// Collects .NET metrics (application, runtime, ASP.NET Core) in memory and renders them in the Prometheus text format.
/// Histograms are exported as count and sum.
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    private static readonly HashSet<string> Meters = new(StringComparer.Ordinal)
    {
        KscTelemetry.MeterName,
        "System.Runtime",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Diagnostics",
    };

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(string Name, string Labels), Series> _series = new();
    private readonly ConcurrentDictionary<string, InstrumentInfo> _instruments = new(StringComparer.Ordinal);

    public MetricsCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Meters.Contains(instrument.Meter.Name))
            {
                _instruments.TryAdd(instrument.Name, new InstrumentInfo(Kind(instrument), instrument.Description));
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<int>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<double>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<float>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<short>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<byte>((i, m, t, _) => Record(i, m, t));
        _listener.SetMeasurementEventCallback<decimal>((i, m, t, _) => Record(i, (double)m, t));
        _listener.Start();
    }

    public string Render()
    {
        _listener.RecordObservableInstruments();

        var text = new StringBuilder();
        foreach (var group in _series.GroupBy(s => s.Key.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var info = _instruments.TryGetValue(group.Key, out var found) ? found : new InstrumentInfo(MetricKind.Gauge, string.Empty);
            var name = Sanitize(group.Key);
            var exported = info.Kind == MetricKind.Counter ? name + "_total" : name;
            var type = info.Kind switch
            {
                MetricKind.Counter => "counter",
                MetricKind.Histogram => "summary",
                _ => "gauge",
            };

            if (!string.IsNullOrWhiteSpace(info.Description))
            {
                text.Append("# HELP ").Append(exported).Append(' ').Append(info.Description.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
            }

            text.Append("# TYPE ").Append(exported).Append(' ').Append(type).Append('\n');
            foreach (var entry in group.OrderBy(s => s.Key.Labels, StringComparer.Ordinal))
            {
                var labels = entry.Key.Labels.Length == 0 ? string.Empty : "{" + entry.Key.Labels + "}";
                var (value, count, sum) = entry.Value.Snapshot();
                if (info.Kind == MetricKind.Histogram)
                {
                    text.Append(exported).Append("_count").Append(labels).Append(' ').Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    text.Append(exported).Append("_sum").Append(labels).Append(' ').Append(sum.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                }
                else
                {
                    text.Append(exported).Append(labels).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    public void Dispose() => _listener.Dispose();

    private static MetricKind Kind(Instrument instrument)
    {
        var type = instrument.GetType().Name;
        return type.StartsWith("Histogram", StringComparison.Ordinal)
            ? MetricKind.Histogram
            : type.Contains("Counter", StringComparison.Ordinal) && !type.StartsWith("UpDown", StringComparison.Ordinal) && !type.StartsWith("ObservableUpDown", StringComparison.Ordinal)
                ? MetricKind.Counter
                : MetricKind.Gauge;
    }

    private void Record(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var labels = string.Join(',', tags.ToArray().OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{Sanitize(t.Key)}=\"{Escape(Convert.ToString(t.Value, CultureInfo.InvariantCulture))}\""));
        var series = _series.GetOrAdd((instrument.Name, labels), _ => new Series());
        var type = instrument.GetType().Name;
        series.Add(measurement, absolute: type.StartsWith("Observable", StringComparison.Ordinal) || type.StartsWith("Gauge", StringComparison.Ordinal));
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or ':' ? c : '_').ToArray();
        var result = new string(chars);
        return result.Length > 0 && char.IsAsciiDigit(result[0]) ? "_" + result : result;
    }

    private static string Escape(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private enum MetricKind
    {
        Counter,
        Gauge,
        Histogram,
    }

    private sealed record InstrumentInfo(MetricKind Kind, string? Description);

    private sealed class Series
    {
        private readonly object _gate = new();
        private double _value;
        private long _count;
        private double _sum;

        public void Add(double measurement, bool absolute)
        {
            lock (_gate)
            {
                _value = absolute ? measurement : _value + measurement;
                _count++;
                _sum += measurement;
            }
        }

        public (double Value, long Count, double Sum) Snapshot()
        {
            lock (_gate)
            {
                return (_value, _count, _sum);
            }
        }
    }
}

public static class MetricsEndpoint
{
    /// <summary>
    /// Maps GET /metrics, protected by the bearer token in Metrics:Token. Without a configured token the endpoint does not exist,
    /// so metrics are never exposed by accident.
    /// </summary>
    public static IEndpointRouteBuilder MapKscMetrics(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        var token = configuration["Metrics:Token"];
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
        {
            return endpoints;
        }

        var expected = Encoding.UTF8.GetBytes(token);
        endpoints.MapGet("/metrics", (HttpContext http, MetricsCollector collector) =>
        {
            var header = http.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetBytes(header[7..].Trim()) : [];
            return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected)
                ? Results.Text(collector.Render(), "text/plain; version=0.0.4; charset=utf-8")
                : Results.Unauthorized();
        }).ExcludeFromDescription();
        return endpoints;
    }
}
