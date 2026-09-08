using Sentry;
using SeattleCarsInBikeLanes.Mobile.Core.Performance;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface IMobileMetricsEmitter
{
    void Emit(MobileMetricEvent metric);
}

public sealed class SentryMobileMetricsEmitter : IMobileMetricsEmitter
{
    public void Emit(MobileMetricEvent metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        KeyValuePair<string, object>[] attributes = metric.Attributes
            .Select(attribute => new KeyValuePair<string, object>(attribute.Key, attribute.Value))
            .ToArray();

        switch (metric.Kind)
        {
            case MobileMetricKind.Counter:
                SentrySdk.Metrics.EmitCounter(metric.Name, metric.Value, attributes);
                break;
            case MobileMetricKind.Distribution:
                SentrySdk.Metrics.EmitDistribution(
                    metric.Name,
                    metric.Value,
                    metric.Unit == MobileMetricUnit.Millisecond
                        ? MeasurementUnit.Duration.Millisecond
                        : MeasurementUnit.None,
                    attributes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric.Kind, null);
        }
    }
}
