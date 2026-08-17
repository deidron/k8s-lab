using System.Net;

namespace K8sLab.Tests;

/// <summary>
/// Guards the wiring the deployment depends on: the probe target, the scrape
/// endpoint and the counter. Breaking any of these leaves the application
/// running and answering requests, so nothing else would catch it.
/// </summary>
public sealed class ObservabilityTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Healthz_answers_and_stays_silent()
    {
        HttpResponseMessage response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_built_in_dotnet_metrics()
    {
        string metrics = await _client.GetStringAsync("/metrics");

        // The dashboards are built on these two: the RED panels read the
        // histogram, the memory panel reads the GC gauge.
        Assert.Contains("http_server_request_duration_seconds", metrics);
        Assert.Contains("dotnet_gc", metrics);
    }

    [Fact]
    public async Task Info_requests_increment_the_counter()
    {
        await _client.GetAsync("/api/info");
        await _client.GetAsync("/api/info");

        string metrics = await _client.GetStringAsync("/metrics");

        // The node label is what tells instances apart on the dashboard.
        Assert.Contains("api_info_requests_total", metrics);
        Assert.Contains("node=\"test-node\"", metrics);
    }
}
