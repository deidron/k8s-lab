using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace K8sLab.Tests;

public sealed class InfoEndpointTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Info_returns_node_pod_and_env()
    {
        JsonElement body = await _client.GetFromJsonAsync<JsonElement>("/api/info");

        Assert.Equal("test", body.GetProperty("env").GetString());
        Assert.Equal("test-node", body.GetProperty("node_name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("pod_name").GetString()));
    }

    [Fact]
    public async Task Info_is_served_under_the_api_group()
    {
        // The route lives at /info inside a group mounted on /api, so a
        // mistake in either half would leave the endpoint at a wrong path.
        HttpResponseMessage grouped = await _client.GetAsync("/api/info");
        HttpResponseMessage ungrouped = await _client.GetAsync("/info");

        Assert.Equal(HttpStatusCode.OK, grouped.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ungrouped.StatusCode);
    }

    [Fact]
    public async Task Root_is_not_mapped()
    {
        HttpResponseMessage response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
