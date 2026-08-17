using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace K8sLab.Tests;

/// <summary>
/// Boots the real application in memory.
/// </summary>
/// <remarks>
/// The environment variables are set before the host starts, because the
/// application reads them during startup:
/// <list type="bullet">
/// <item>DOTNET_RUNNING_IN_CONTAINER disables the NLog Loki target, so test
/// runs do not push log lines into the real Loki.</item>
/// <item>DEPLOY_ENV labels anything that does escape — traces reaching a
/// running Tempo — as env=test, keeping it out of the dev and local views.</item>
/// </list>
/// </remarks>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");
        Environment.SetEnvironmentVariable("DEPLOY_ENV", "test");
        Environment.SetEnvironmentVariable("KUBE_NODE_NAME", "test-node");

        return base.CreateHost(builder);
    }
}
