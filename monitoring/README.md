# Observability: logs, metrics, traces

A dev stand on Docker Desktop. Three signals, one Grafana.

```
cluster:  pods ──logs───────> Alloy ──┐   logs    — read through the API server
              └─metrics─────> Alloy ──┤   metrics — scraped by annotations
          application ─traces> Alloy ──┤   traces  — received over OTLP
                                      │
Windows (docker compose):             ↓
          Loki  :3100      ←─── logs
          Prometheus :9090 ←─── metrics (remote write)
          Tempo :4317      ←─── traces (OTLP)
                  └──> Grafana :3000

local run (no Alloy involved):
          NLog ──────────────> Loki :3100        (loki target in nlog.config)
          Prometheus ─scrape─> host :5103        (static target)
          OpenTelemetry ─────> Tempo :4317       (OTLP_ENDPOINT default)
```

Everything from the cluster passes through Alloy. It is the single point of
failure inside the cluster — but it is also the single place where labels are
configured, so logs, metrics and traces describe objects the same way.

An instance run locally from the IDE reaches the same backends without Alloy:
it is not a pod, so there is nothing to discover and no Kubernetes metadata to
attach. Its labels are set by hand at each source.

## The env label

Separates cluster data from a locally started application. It works across all
three signals, but is set in different places:

| Source | Where it is set | Value |
|---|---|---|
| Cluster logs and metrics | rule in [alloy/config.alloy](alloy/config.alloy) | `dev` |
| Traces | `DEPLOY_ENV` variable, see `k8s/base/deployment.yaml` | `dev` / `prod` |
| Local logs | the `loki` target in `nlog.config` | `local` |
| Local metrics | static target in [prometheus.yml](prometheus.yml) | `local` |
| Local traces | the default value in `Program.cs` | `local` |

Queries, respectively:

```logql
{app="k8s-lab", env="dev"}
```

```promql
up{env="dev"}
```

```
{resource.env="dev"}
```

When moving to another cluster the value changes in two places: the `env` rule
in the Alloy config and the `DEPLOY_ENV` variable in the application manifest.

## Why the stand lives outside the cluster

Loki, Prometheus, Tempo and Grafana store data. The Docker Desktop cluster is
easy to recreate, and losing the history along with it is not acceptable — this
has already been proven in practice: the cluster disappeared and the stand kept
its data. As a bonus the ports are published straight onto Windows localhost,
so the socat proxies (see [../k8s/DEV-WINDOWS.md](../k8s/DEV-WINDOWS.md)) are
not needed here.

Alloy stays inside the cluster: pod logs live inside the node container under
containerd and are invisible to the outer Docker daemon, and metrics have to be
scraped at pod addresses. Alloy holds no state and survives cluster recreation.

**Promtail is not used** — Grafana declared it deprecated and support ended in
February 2026. Alloy is the official replacement, and it collects metrics too,
so it replaces the Prometheus agent as well.

## Startup

```bash
docker compose -f monitoring/docker-compose.yml up -d
```

```bash
kubectl apply -k monitoring/alloy
```

Grafana is at `http://localhost:3000` and asks for no login — anonymous access
with Admin rights is enabled deliberately, this is a dev stand. All three data
sources come from provisioning.

Start order is set by a plain `depends_on`, with no readiness gate: Grafana
queries data sources lazily, when a dashboard is opened, so a Loki that is not
up yet does not kill it.

## Logs

NLog writes JSON to stdout
([../src/K8sLab/nlog.config](../src/K8sLab/nlog.config)), so the fields
parse immediately:

```logql
{app="k8s-lab"} | json | log_level="ERROR"
```

Labels are set by Alloy from Kubernetes metadata: `namespace`, `app`, `pod`,
`node`, `container`, `env`. The application knows nothing about them. Fields
from the JSON are **not** labels — they are parsed on the fly by the `| json`
operator.

Loki indexes labels, not content. Hence the rule: few labels, all with low
cardinality. Putting a request id into a label is a reliable way to kill Loki.

## Metrics

The application exposes `/metrics` in Prometheus format. What is in there:

- built-in platform metrics (.NET 8+ publishes them itself) — HTTP request
  duration and count, Kestrel connections, GC, thread pool
- the application's own `api_info_requests_total` counter with a `node` label

Alloy selects pods by the annotations in
[../k8s/base/deployment.yaml](../k8s/base/deployment.yaml):

```yaml
prometheus.io/scrape: "true"
prometheus.io/port: "8080"
prometheus.io/path: "/metrics"
```

This is a common convention, not something specific to Alloy — a plain
Prometheus works off the same annotations.

What is collected goes into Prometheus over remote write. The receiver is
enabled by the `--web.enable-remote-write-receiver` flag, set in compose.

### Prometheus scrapes nothing here

Architecturally Prometheus is a pull system: it walks a list of targets itself.
On this stand it has exactly one target — itself:

```
job: prometheus   http://localhost:9090/metrics
```

Everything else arrives from outside. This is forced: Prometheus lives on
Windows, the pods are in the `10.244.0.0/16` subnet inside the node container,
and there is no route there. Alloy scrapes from the inside and pushes outward.
The same reason as for the socat proxy.

**The price:** nobody watches Alloy itself. Pod liveness is tracked fine — the
`up` metric is generated by `prometheus.scrape` for every target and shipped
along with the rest:

```
up{app="k8s-lab", instance="10.244.0.6:8080", job="prometheus.scrape.pods"} 1
```

But nobody scrapes Alloy. If it dies, the `up` series simply stop updating, and
telling that apart from normal operation is only possible by data age — via
`absent()` or a `timestamp()` check on the latest sample.

In production the scheme flips back: Prometheus moves inside the cluster and
scrapes pods directly, as intended. Alloy stays on logs only.

Example queries (PromQL):

```promql
rate(api_info_requests_total[5m])
```

```promql
histogram_quantile(0.95, rate(http_server_request_duration_seconds_bucket[5m]))
```

## Dashboards

They live as files in [grafana-dashboards/](grafana-dashboards/) and are wired
in through [grafana-dashboard-provider.yaml](grafana-dashboard-provider.yaml).
Edits to the JSON are picked up within 30 seconds, no Grafana restart needed.

Files rather than drawing in the UI: anything drawn lives in the `grafana-data`
volume and disappears when it is recreated — which already happened while
sorting out data source `uid`s.

**Service — RED** (`service-red`): request rate, share of 5xx, latency
quantiles, requests by route, the application's own counter by pod, .NET memory,
plus an error graph from the logs, a table of recent traces and a live log feed.
The `env` switch at the top selects the environment — `dev`, `local` or all.

**Observability pipeline** (`observability-pipeline`): target availability,
Alloy config state, sent versus dropped log entries, the trace exporter queue,
and Alloy's own logs. It catches the "no data because the collector broke"
situation, which is otherwise easy to mistake for no traffic.

Off-the-shelf dashboards from grafana.com are better left alone here: they
assume their own label sets, and half the panels would come up empty.

Panels mixing data sources are the point — one dashboard can hold Prometheus,
Loki and Tempo panels side by side.

### Metric names

Prometheus 3.x supports UTF-8 in names and takes them from OTel as is, with dots
(`http.server.request.duration_seconds_count`), while Alloy asks for the escaped
form when scraping and gets underscores. Without aligning them one metric would
sit in the database under two names, and panels would break when switching `env`.

That is why the local target in [prometheus.yml](prometheus.yml) sets
`metric_name_escaping_scheme: underscores`. All dashboard queries use
underscores.

### Stat panels and the instant flag

A stat panel takes the **last non-empty value over the whole range**, not the
current one. Series from deleted Alloy pods linger in a three-hour window, so
the panel would show several values at once ("yes yes yes", "11 7 11").

The fix is `"instant": true` on the target plus an aggregation. The functions
are chosen by meaning: `min` for the config-loaded flag, so it reads NO if at
least one agent failed to come up; `max` for counters, because summing counters
across instances is meaningless — they reset when a pod restarts.

## Traces

ASP.NET Core creates an `Activity` for every request — the trace already exists,
it only needs to be shipped. OpenTelemetry in
[../src/K8sLab/Program.cs](../src/K8sLab/Program.cs) exports it over
OTLP.

The address comes from the `OTLP_ENDPOINT` variable: in the cluster
`http://alloy.monitoring.svc.cluster.local:4317`, locally
`http://localhost:4317` (the default — then spans go to Tempo directly).

From the cluster traces go to Alloy, not to Tempo. It accepts OTLP, adds
Kubernetes labels to the spans with the `k8sattributes` processor, and forwards
them on. Without that hop a trace carries only HTTP attributes and
`service.name`, and cannot be filtered by pod or namespace — unlike logs and
metrics, which do have those labels.

To check that enrichment works:

```bash
curl.exe -s http://localhost:3200/api/traces/TRACE_ID
```

The response should contain `k8s.pod.name`, `k8s.namespace.name`,
`k8s.node.name` and `k8s.deployment.name`.

The application does not know the external host address — nothing to change in
it when moving to production, only the Tempo address in the Alloy config.

Scrapes of `/metrics` are excluded from tracing by a filter in the ASP.NET Core
instrumentation: they arrive every minute around the clock and would drown out
the useful requests.

The main payoff is the link with logs. A `trace_id` field was added to the JSON
log, and the Loki data source is configured with `derivedFields`, which
recognises it and turns it into a link into Tempo. Debugging flow: spot an error
in the logs → click → see the whole request path with timings.

With a single service a trace consists of one span, so the practical value
appears once there are more services. The mechanism is already set up and moves
over unchanged.

## Data source order in Grafana

The files in [grafana-datasources/](grafana-datasources/) are numbered for a
reason: Grafana reads the directory alphabetically and validates references
between data sources during provisioning. Loki references Tempo through
`derivedFields`, so Tempo is described in `01-backends.yaml` and Loki in
`02-loki.yaml`.

The second trap is in the same place — variable substitution. Grafana expands
`${...}` in provisioning files as environment variables, so the value template
in `derivedFields` is written with a doubled dollar: `$${__value.raw}`. With a
single one, `url` is stored empty, the TraceID field still appears in the logs,
but the link opens Tempo with an empty query and a "No data" message. To check
what was stored:

```bash
curl.exe -s http://localhost:3000/api/datasources/uid/loki
```

If you change the `uid` of an already created data source, Grafana crashes with
`Datasource provisioning error: data source not found` — a record with the old
uid stays in its database. The cure is recreating the volume:

```bash
docker compose -f monitoring/docker-compose.yml rm -sf grafana && docker volume rm monitoring_grafana-data
```

Dashboards come back from provisioning, so this is safe — but anything drawn by
hand in the UI is lost.

## Maintenance

**Changed the Alloy config** ([alloy/config.alloy](alloy/config.alloy)) — just
apply it:

```bash
kubectl apply -k monitoring/alloy
```

No need to restart the pod by hand: the ConfigMap name carries a hash of its
content, so the reference in the Deployment changes and Kubernetes recreates the
pod itself.

**Recreated the cluster** — the stand is untouched, only bring the collector
back with the same `kubectl apply -k monitoring/alloy`.

Warnings like `could not get pod info; will retry tailing` right after a rollout
are normal: the log tailer loses a pod that has just been deleted and reconnects
within seconds.

**Diagnostics.** In order:

```bash
kubectl logs -n monitoring deployment/alloy --tail=50
```

```bash
curl.exe -s http://localhost:3100/loki/api/v1/labels
curl.exe -s "http://localhost:9090/api/v1/label/__name__/values"
```

Host reachability from the cluster — the whole scheme rests on it:

```bash
kubectl run nettest --rm -i --restart=Never --image=curlimages/curl -- curl -s -o /dev/null -w "%{http_code}\n" http://host.docker.internal:3100/ready
```

**Disk space.** Logs — one week (`retention_period` in
[loki-config.yaml](loki-config.yaml)), metrics — one week
(`--storage.tsdb.retention.time=7d`), traces — the Tempo default.

**Wipe all data and start clean:**

```bash
docker compose -f monitoring/docker-compose.yml down -v && docker compose -f monitoring/docker-compose.yml up -d
```

Data sources and dashboards come back from the provisioning files. Alloy does
not replay history, so the deleted telemetry is gone for good.

**Stop:**

```bash
docker compose -f monitoring/docker-compose.yml down
```

Without `-v` the data stays in the volumes.

## What changes in production

The topology stays the same; only the storage moves — onto the 24/7 host or a
separate VM. The host address changes in the Alloy config and in the
`OTLP_ENDPOINT` variable; queries and labels stay as they are.

Must be fixed for production:

- remove anonymous access to Grafana (`GF_AUTH_ANONYMOUS_ENABLED`)
- pin image versions instead of `latest` — Tempo already broke on this: 3.0
  dropped the top-level `ingester` and `compactor` config sections
- raise retention periods and plan disk space for them
- move `/metrics` onto a separate port, closed off from the outside
- consider a DaemonSet reading `/var/log/pods` instead of a Deployment reading
  through the API server: it puts less load on the API server and does not lose
  the final log lines of a terminating pod
