using System.Diagnostics.Metrics;
using KSC.MeetingManagement.Application.Telemetry;

namespace KSC.MeetingManagement.UnitTests;

public class OutboxQueueMetricsTests
{
    [Fact]
    public void Queue_size_is_reported_only_after_the_first_measurement()
    {
        var seen = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KscTelemetry.MeterName && instrument.Name.StartsWith("ksc.outbox.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, m, _, _) => seen[i.Name] = m);
        listener.SetMeasurementEventCallback<double>((i, m, _, _) => seen[i.Name] = m);
        listener.Start();

        OutboxQueueMetrics.Reset();
        listener.RecordObservableInstruments();
        Assert.Empty(seen);

        OutboxQueueMetrics.Update(pending: 7, dead: 2, oldestPendingAgeSeconds: 90);
        listener.RecordObservableInstruments();

        Assert.Equal(7, seen["ksc.outbox.pending"]);
        Assert.Equal(2, seen["ksc.outbox.dead"]);
        Assert.Equal(90, seen["ksc.outbox.oldest_pending_age_seconds"]);
        OutboxQueueMetrics.Reset();
    }
}
