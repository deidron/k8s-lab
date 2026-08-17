# Cluster access from Windows (Docker Desktop)

How to get a permanent `http://localhost:32000/api/info` on the dev machine.

The Windows-side port deliberately matches `nodePort`: the proxy becomes
transparent, and `localhost:32000` is literally the same address as inside the
cluster, just from outside. The number 8080 never appears on the host side —
it belongs only to the port inside the container.

This is scaffolding around a Docker Desktop quirk. It does not travel to the
k3s production setup, where the service is reachable at the node IP by itself
(see [DEPLOY-PROD.md](DEPLOY-PROD.md)).

---

## The problem

Kubernetes in Docker Desktop is kind-based: the node is a container named
`desktop-control-plane` inside the `kind` docker network (subnet
`172.22.0.0/16`), which lives in WSL2. There is no route into it from Windows.

Consequence: **no Service type gives direct access from the host.**
Verified on a live cluster — all three addresses were unreachable:

```
172.22.0.3:8080  -> no connection    (LoadBalancer EXTERNAL-IP)
localhost:8080   -> no connection
localhost:31330  -> no connection    (nodePort)
```

This is not about Kubernetes or Service settings. Traffic from Windows simply
never reaches the subnet the node sits in.

To see the current layout:

```bash
docker network inspect kind --format '{{range .Containers}}{{.Name}} {{.IPv4Address}}{{"\n"}}{{end}}'
```

---

## The solution

A `socat` container is placed **inside** the `kind` network — the node is
visible from there — and publishes a port outward with `-p`. Docker Desktop
surfaces published ports on Windows localhost. The result is a bridge between
two networks, neither of which is reachable on its own:

```
Windows localhost:32000 ──> [socat] ──> desktop-control-plane:32000 ──> Service ──> pods
                                     └── inside the kind network ──┘
```

### Why NodePort and not LoadBalancer

The intermediary is needed with any Service type, but with NodePort both halves
of the destination address are fixed, while with LoadBalancer neither is.

| | NodePort | LoadBalancer |
|---|---|---|
| Destination address | `desktop-control-plane` — node name, stable | `EXTERNAL-IP` — changes on restart |
| Port | `32000`, pinned in the manifest | assigned at random from 30000–32767 |
| Binding by DNS name | works | no: container name is `kindccm-<random>` |

Docker hands out addresses in container start order, so they get reshuffled
after a restart. Observed within a single session: the node moved from
`172.22.0.5` to `172.22.0.2`, and `EXTERNAL-IP` from `172.22.0.3` to
`172.22.0.5`. A proxy pointed at an IP dies after that; one pointed at a name
keeps working.

---

## Setup

### 1. Apply the NodePort manifest

The port is pinned in the overlay (`nodePort: 32000`), nothing to edit:

```bash
kubectl apply -k k8s/overlays/dev-nodeport
```

### 2. Start the proxy

```bash
docker run -d --name k8s-lab-proxy --restart unless-stopped --network kind -p 32000:32000 alpine/socat tcp-listen:32000,fork,reuseaddr tcp:desktop-control-plane:32000
```

Argument by argument:

- `--network kind` — join the network where the node is visible. Without it the
  whole point is lost
- `-p 32000:32000` — expose the port on Windows under the same number as nodePort
- `--restart unless-stopped` — survives a reboot, no need to start it by hand
- `tcp-listen:32000,fork` — `fork` is mandatory: without it socat serves one
  connection and exits
- `reuseaddr` — lets the container restart without waiting for the port to free up

The name `k8s-lab-proxy` is arbitrary and can be changed.

### 3. Verify

```bash
curl.exe -s http://localhost:32000/api/info
```

Expected:

```json
{"env":"dev","node_name":"desktop-control-plane","pod_name":"k8s-lab-deployment-..."}
```

`/api/info` (see [InfoEndpoints.cs](../src/K8sLab/InfoEndpoints.cs)) and
`/metrics` and `/healthz` are the only routes the application serves. `/`
returns 404, which is normal.

---

## Checking load balancing

kube-proxy distributes **connections, not requests**, so you need a loop that
opens a new connection each iteration. In PowerShell:

```powershell
1..12 | ForEach-Object { (curl.exe -s http://localhost:32000/api/info | ConvertFrom-Json).pod_name } | Group-Object | Select-Object Count, Name
```

With two replicas the responses split roughly evenly between pods.

You cannot see this from a browser: keep-alive holds a single connection, so
every page refresh lands on the same pod. Not a fault — expected behaviour of
L4 balancing.

---

## Why not port-forward

`kubectl port-forward` works too, but it serves a different purpose:

```bash
kubectl port-forward service/k8s-lab-service 32001:80
```

A different port on purpose: if the proxy is running, 32000 is already taken.

It resolves the selector once, **picks a single pod** and tunnels all traffic
into it through the API server. Neither the Service nor kube-proxy takes part.
Measured on a live cluster: 12 requests out of 12 came from one pod, whereas
through the Service the split was 6/6.

It also needs an open terminal and breaks if the chosen pod dies.

Good for debugging one specific instance. For permanent access — use the proxy.

---

## Maintenance

**Recreated the cluster** (Reset Kubernetes Cluster in Docker Desktop) — the
`kind` network is created anew and the proxy loses its connection:

```bash
docker restart k8s-lab-proxy
```

**The node is named differently.** `desktop-control-plane` is the Docker Desktop
name. In standalone kind it is `<cluster-name>-control-plane`. Check with:

```bash
kubectl get nodes
```

**Port 32000 is taken.** Change the left half of `-p`, e.g. `-p 32005:32000` —
the service then lives at `localhost:32005`. Leave the right half alone, it
points at the nodePort inside the cluster.

**Close off access from the local network.** By default the port is published on
all interfaces (`0.0.0.0`), so the API is visible to everyone on the Wi-Fi.
Bind it to localhost only:

```bash
docker rm -f k8s-lab-proxy && docker run -d --name k8s-lab-proxy --restart unless-stopped --network kind -p 127.0.0.1:32000:32000 alpine/socat tcp-listen:32000,fork,reuseaddr tcp:desktop-control-plane:32000
```

**Diagnostics.** If `localhost:32000` does not answer, check in this order:

```bash
docker ps --filter name=k8s-lab-proxy          # is the container alive?
docker logs k8s-lab-proxy                       # is socat complaining?
kubectl get svc k8s-lab-service           # is nodePort really 32000?
kubectl get pods -l app=k8s-lab           # pods Running and READY 1/1?
```

**Remove it entirely:**

```bash
docker rm -f k8s-lab-proxy
```

---

## What of this moves to production

Nothing. On k3s in a VM the node is a real machine on the home network, and the
service is reachable at its address directly: `192.168.1.50:32000` for NodePort,
or simply `192.168.1.50` for LoadBalancer, which genuinely works there thanks to
servicelb.

The one habit worth carrying over is pinning `nodePort` by hand instead of
letting it be random. With a random port you can neither set up forwarding nor
write the address into a config.
