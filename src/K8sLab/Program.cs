using K8sLab;
using NLog.Web;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Host.UseNLog();
string otlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT")
    ?? "http://localhost:4317";
string deployEnv = Environment.GetEnvironmentVariable("DEPLOY_ENV")
    ?? "local";
builder.Services.AddSingleton<AppMetrics>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService("k8s-lab")
        .AddAttributes(new KeyValuePair<string, object>[] { new("env", deployEnv) }))
    .WithMetrics(m => m
        .AddMeter("Microsoft.AspNetCore.Hosting")
        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
        .AddMeter("System.Runtime")
        .AddMeter(AppMetrics.MeterName)
        .AddPrometheusExporter())
    .WithTracing(t => t
        // Infrastructure traffic is excluded: /metrics is scraped every
        // minute and /healthz is probed every few seconds, around the clock.
        // Their spans carry no information and would bury the real requests.
        .AddAspNetCoreInstrumentation(o =>
            o.Filter = static ctx =>
                !ctx.Request.Path.StartsWithSegments("/metrics") &&
                !ctx.Request.Path.StartsWithSegments("/healthz"))
        // No HttpClient in the code yet. Kept so that the moment this
        // service starts calling another one, the trace continues into it
        // instead of ending at the process boundary.
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));
WebApplication app = builder.Build();
app.MapPrometheusScrapingEndpoint();

// Target for the readiness and liveness probes. Deliberately silent: it is
// called every few seconds forever, and logging that would drown the logs.
app.MapGet("/healthz", static () => Results.Ok());

app.MapGroup("/api")
   .MapInfoEndpoints();
app.Run();
