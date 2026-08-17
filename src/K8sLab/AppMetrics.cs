using System.Diagnostics.Metrics;

namespace K8sLab;

public sealed class AppMetrics
{
    public const string MeterName = "K8sLab";

    private readonly Counter<long> _infoRequests;

    public AppMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(MeterName);
        _infoRequests = meter.CreateCounter<long>(
            "api_info_requests_total",
            unit: "requests",
            description: "Number of requests to /api/info");
    }

    public void InfoRequested(string nodeName) =>
        _infoRequests.Add(1, new KeyValuePair<string, object?>("node", nodeName));
}
