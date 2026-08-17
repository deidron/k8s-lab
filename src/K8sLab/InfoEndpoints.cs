namespace K8sLab;

public static class InfoEndpoints
{
    public static RouteGroupBuilder MapInfoEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/info", static (ILogger<Program> log, AppMetrics metrics) =>
        {
            string nodeName = Environment.GetEnvironmentVariable("KUBE_NODE_NAME") ?? "Local";
            string podName = Environment.GetEnvironmentVariable("HOSTNAME") ?? "Unknown";
            // Same value as the env label on logs, metrics and traces, so the
            // response tells you which filter finds this request in Grafana.
            string env = Environment.GetEnvironmentVariable("DEPLOY_ENV") ?? "local";

            metrics.InfoRequested(nodeName);
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Request processed on node {NodeName} inside pod {PodName}", nodeName, podName);

            return Results.Ok(new { env, node_name = nodeName, pod_name = podName });
        });
        return group;
    }
}
