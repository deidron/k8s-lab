# k8s-lab

[![CI](https://github.com/deidron/k8s-lab/actions/workflows/ci.yml/badge.svg)](https://github.com/deidron/k8s-lab/actions/workflows/ci.yml)

An ASP.NET Core service wired end to end for observability — structured logs,
metrics and distributed tracing in Loki, Prometheus and Tempo. The same
backends serve both the Kubernetes deployment, where Grafana Alloy collects
everything, and an instance started locally from the IDE, which reports to them
directly; an `env` label keeps the two apart. The service itself stays minimal
on purpose — one endpoint reporting which node and pod answered, plus `/metrics`
for scraping and `/healthz` for probes — so the
manifests, the collection pipeline and the path from a laptop to a home
production cluster are what the repository is about.

The setup targets development: it runs on Docker Desktop, Grafana has no
authentication and the backends are single instances on local disk. What has to
change before any of this faces real traffic is listed in
[monitoring/README.md](monitoring/README.md) and
[k8s/DEPLOY-PROD.md](k8s/DEPLOY-PROD.md).

```
GET /api/info
{"env":"dev","node_name":"desktop-control-plane","pod_name":"k8s-lab-deployment-59c99d8bfd-424fk"}
```

Returning the node and pod names is the point: it makes load balancing,
rollouts and scheduling visible without any extra tooling.

## Layout

```
src/K8sLab/        .NET 10 minimal API, Dockerfile, NLog and OpenTelemetry setup
k8s/                   kustomize manifests: base + overlays for dev and prod
  base/                Deployment and Service shared by every environment
  overlays/            dev-nodeport, dev-loadbalancer, prod
  DEV-WINDOWS.md       reaching the cluster from Windows on Docker Desktop
  DEPLOY-PROD.md       phased plan for k3s in a Hyper-V VM
monitoring/            Loki, Prometheus, Tempo, Grafana in compose; Alloy in-cluster
  README.md            how the three signals are collected and why
```

## Quick start

Development runs against Kubernetes in Docker Desktop.

Build the image and deploy it. The tag must be bumped on every build —
Kubernetes will not re-pull an image tag a node already holds:

```bash
docker build -f src/K8sLab/Dockerfile -t k8s-lab:v1 src/K8sLab
```

```bash
kubectl apply -k k8s/overlays/dev-nodeport
```

Pods are not reachable from Windows directly — the cluster network lives inside
WSL2. A small socat container bridges the gap; see
[k8s/DEV-WINDOWS.md](k8s/DEV-WINDOWS.md) for the reasoning:

```bash
docker run -d --name k8s-lab-proxy --restart unless-stopped --network kind -p 32000:32000 alpine/socat tcp-listen:32000,fork,reuseaddr tcp:desktop-control-plane:32000
```

```bash
curl.exe -s http://localhost:32000/api/info
```

Then bring up the observability stack and the collector:

```bash
docker compose -f monitoring/docker-compose.yml up -d
```

```bash
kubectl apply -k monitoring/alloy
```

Grafana is at `http://localhost:3000` with two provisioned dashboards and all
three data sources already wired up.

## Running locally

The `http` profile listens on port 5103 and is connected to the same
observability stack: traces go straight to Tempo, logs are pushed to Loki by an
NLog target, and Prometheus scrapes the host. All three carry `env=local`,
which separates them from cluster data labelled `env=dev`.

```bash
dotnet run --project src/K8sLab --launch-profile http
```

## Observability

The storage backends run outside the cluster and receive from two independent
sources: the cluster, where everything goes through Grafana Alloy, and the
locally run instance, which reaches them directly.

| Signal | From the cluster | From a local run |
|---|---|---|
| Logs | NLog writes JSON to stdout → Alloy reads via the API server → Loki | NLog pushes to Loki over HTTP |
| Metrics | `/metrics` → Alloy scrapes by annotation → Prometheus | Prometheus scrapes the host directly |
| Traces | OTLP → Alloy enriches with pod metadata → Tempo | OTLP straight to Tempo |

The two are told apart by the `env` label — `dev` against `local` — which works
in all three signals, so one dashboard covers both.

Log lines carry a `trace_id`, and the Loki data source turns it into a link
into Tempo, so an error in the logs leads to the trace of that exact request in
one click.

Keeping the storage outside the cluster is deliberate: monitoring should not
disappear together with the thing it monitors. The full rationale, the label
scheme and the operational notes are in
[monitoring/README.md](monitoring/README.md).

## Production

[k8s/DEPLOY-PROD.md](k8s/DEPLOY-PROD.md) is a phased plan for running this on
k3s in a Hyper-V virtual machine: VM setup, a fixed address, k3s, an image
registry, the production overlay, and optional external access through a
Cloudflare Tunnel.

Nothing from the Windows development setup carries over — the socat proxy and
the pinned NodePort exist only to work around Docker Desktop networking. On k3s
the service is reachable at the node address directly.

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push to
`main`, on `v*` tags and on pull requests:

- **build and test** — six integration tests boot the application in memory and
  check the routes plus the observability wiring
- **validate manifests** — every kustomize overlay is rendered, both dashboards
  are parsed and the compose file is checked. A broken patch or a dashboard with
  invalid JSON otherwise surfaces only at apply time, or never: Grafana skips a
  dashboard it cannot parse without saying so
- **build and publish the image** — every run builds it, so a broken Dockerfile
  fails the pull request rather than the release, but only a `v*` tag pushes it
  to GHCR, as `ghcr.io/deidron/k8s-lab:<tag>` and `:<commit-sha>`

Both names point at the same image, and neither ever moves. Deploying means
putting one of them into the `images:` block of the prod overlay, which also
makes the running version traceable back to a commit.

A tag can be placed on any commit, so publishing first checks that the tagged
commit is reachable from `main` and fails the run when it is not — a tag on an
unreviewed branch would otherwise ship as a release. Jobs carry timeouts, and
pull request runs are superseded by the next push rather than piling up.

[`.github/dependabot.yml`](.github/dependabot.yml) raises weekly pull requests
for NuGet packages, GitHub Actions and the Docker base images. Related packages
are grouped, so an OpenTelemetry bump arrives as one PR rather than six — the
set expects matching versions. Every such PR goes through the same checks
before it can be merged.

## Conventions worth knowing

**Manifests are built with kustomize.** The base holds what every environment
shares; overlays add only their differences. Never edit rendered output — check
what an overlay produces with `kubectl kustomize k8s/overlays/prod`.

**Dashboards and data sources are files.** Anything created through the Grafana
UI lives only in a Docker volume and is lost when it is recreated.

**Image tags are versions, never reused.** `latest` cannot be rolled back to,
and a reused tag makes a rollout silently keep the previous build.

**Labels are low-cardinality.** Loki indexes labels rather than log content, so
identifiers belong in the log body, not in a label.
