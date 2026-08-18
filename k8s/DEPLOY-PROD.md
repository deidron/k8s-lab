# Deploying k8s-lab on k3s in Hyper-V

A working plan for a home production setup. Target architecture:

```
Hyper-V host (24/7, Ethernet)
├── VM: Home Assistant        — untouched, keeps running as before
└── VM: k8s-prod (Ubuntu 24.04 + k3s)
        └── k8s-lab ← image from GitHub Container Registry
```

External access (phase 7) goes through a Cloudflare Tunnel, because the ISP is
behind CGNAT and port forwarding on the Keenetic will not work. Home Assistant
stays on Nabu Casa; it does not move into the cluster and does not depend on it.

Phases 1–6 are mandatory and give a working cluster on the local network.
Phases 7–9 are optional.

---

## Phase 0. What you need up front

- Ubuntu Server 24.04 LTS ISO (not Desktop).
- A GitHub account — for GitHub Container Registry, free.
- A free IP on the home network for the VM. `192.168.1.50` is used below,
  substitute your own.
- The name of the Hyper-V external virtual switch that Home Assistant uses.
  Check on the host:

```powershell
Get-VMSwitch | Select-Object Name, SwitchType
```

You want the one with `SwitchType = External`. No need to create a new one.

---

## Phase 1. The Hyper-V virtual machine

All commands run on the host, in PowerShell **as administrator**.

### 1.1 Creation

```powershell
New-VM -Name k8s-prod -Generation 2 -MemoryStartupBytes 6GB `
  -NewVHDPath "D:\Hyper-V\k8s-prod\k8s-prod.vhdx" -NewVHDSizeBytes 60GB `
  -SwitchName "EXTERNAL_SWITCH_NAME"
```

Adjust the disk path to your own. 60 GB leaves room for container images —
they accumulate faster than you expect.

### 1.2 Mandatory settings

Secure Boot defaults to the Windows template and will not let Ubuntu boot.
Switch the template to UEFI CA:

```powershell
Set-VMFirmware -VMName k8s-prod -SecureBootTemplate MicrosoftUEFICertificateAuthority
```

Memory must be static. Dynamic memory breaks kubelet: it reads available memory
at startup and reacts badly when it is taken away at runtime.

```powershell
Set-VMMemory -VMName k8s-prod -DynamicMemoryEnabled $false -StartupBytes 6GB
Set-VMProcessor -VMName k8s-prod -Count 4
```

Auto-start with a delay so Home Assistant comes up first and does not compete
for disk. Turn off automatic checkpoints — they are on by default and quietly
eat the disk with snapshots.

```powershell
Set-VM -Name k8s-prod -AutomaticCheckpointsEnabled $false `
  -AutomaticStartAction Start -AutomaticStartDelay 60 -AutomaticStopAction ShutDown
```

### 1.3 Starting the installation

```powershell
Add-VMDvdDrive -VMName k8s-prod -Path "C:\ISO\ubuntu-24.04-live-server-amd64.iso"
Set-VMFirmware -VMName k8s-prod -FirstBootDevice (Get-VMDvdDrive -VMName k8s-prod)
Start-VM -Name k8s-prod
vmconnect.exe localhost k8s-prod
```

**Phase check:** the VM booted from the ISO and shows the Ubuntu installer.

---

## Phase 2. Ubuntu and networking

### 2.1 Installation

In the installer: minimal installation, default partitioning, and **make sure to
tick OpenSSH server** — otherwise you are stuck working through the vmconnect
window.

No need to disable swap: k3s works with it, unlike kubeadm.

### 2.2 A fixed address

This is critical. The node address is baked into the cluster certificates during
initialisation; if it changes after a host reboot, the cluster will not come up.

The simplest route is a MAC reservation in the Keenetic web UI (device list →
pin the IP). The VM's MAC:

```powershell
Get-VMNetworkAdapter -VMName k8s-prod | Select-Object MacAddress
```

Or statically inside the guest, `/etc/netplan/50-cloud-init.yaml`:

```yaml
network:
  version: 2
  ethernets:
    eth0:
      dhcp4: no
      addresses: [192.168.1.50/24]
      routes:
        - to: default
          via: 192.168.1.1
      nameservers:
        addresses: [192.168.1.1, 1.1.1.1]
```

```bash
sudo netplan apply
```

### 2.3 Updates and time

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y unattended-upgrades
sudo timedatectl set-timezone Europe/Moscow
```

Clock drift breaks TLS inside the cluster, so the time zone is not cosmetic.

**Phase check:** `ssh user@192.168.1.50` connects, `ip a` shows the expected
address, and after `sudo reboot` the address is the same.

---

## Phase 3. k3s

### 3.1 Installation

```bash
curl -sfL https://get.k3s.io | sh -
```

One minute. The script installs containerd, the CNI, CoreDNS, Traefik, servicelb
and local-path-provisioner, and registers a systemd unit with auto-start.

### 3.2 Access as your own user

The kubeconfig sits at `/etc/rancher/k3s/k3s.yaml` owned by root — as a normal
user `kubectl` will complain about permission denied.

```bash
mkdir -p ~/.kube && sudo cp /etc/rancher/k3s/k3s.yaml ~/.kube/config
sudo chown $USER:$USER ~/.kube/config && chmod 600 ~/.kube/config
```

### 3.3 Access from Windows

Copy that file to the dev machine and replace `127.0.0.1` in it with
`192.168.1.50`. After that `kubectl` on Windows talks to the cluster directly —
no proxying containers as with Docker Desktop.

**Phase check:**

```bash
kubectl get nodes
```

The node is `Ready`. If it is `NotReady`, look at `sudo journalctl -u k3s -f`.

---

## Phase 4. Image registry

Right now the image exists only locally in Docker Desktop. The new VM has
nowhere to get it from, so a registry is needed. GitHub Container Registry is
free for this.

### 4.1 CI publishes it

Publishing a release builds the image and pushes it as both
`ghcr.io/deidron/k8s-lab:<tag>` and `:<commit-sha>` — see
[../.github/workflows/release.yml](../.github/workflows/release.yml). Pull
requests and pushes to `main` build the image too, in
[../.github/workflows/ci.yml](../.github/workflows/ci.yml), but never publish
it, so a broken Dockerfile is caught before a release rather than during one.

Cut the release from `main` once the change is merged:

```bash
gh release create v1.0.0 --target main --generate-notes
```

Nothing else to do by hand. The workflow appends the published image name to
the release notes, so the value to deploy sits next to the release rather than
in a run log; the names also appear under Packages in the repository.

The tag has to sit on a commit that is already in `main` — CI checks this and
fails otherwise, so a release cut from a branch by mistake cannot ship. Pushing
a tag on its own does nothing at all.

### 4.1.1 Check where the image came from

The release also publishes an attestation: a signed statement that this exact
image was built by that workflow from that commit. Anyone with write access to
the registry can push an image by hand, and nothing about the name would look
different — this is what tells the two apart. Run it before pinning a new tag:

```bash
gh attestation verify oci://ghcr.io/deidron/k8s-lab:v1.0.0 --repo deidron/k8s-lab
```

It reports the workflow and commit behind the image, and fails when there is no
attestation to show. It says nothing about whether the code is any good: a
compromised workflow would sign its output just as happily.

To publish from the dev machine anyway — a first push before CI exists, or a
build that is not in `main` — mind the build context: the Dockerfile expects
`src/K8sLab`, not the repository root.

```bash
docker build -f src/K8sLab/Dockerfile -t ghcr.io/deidron/k8s-lab:1.0.0 src/K8sLab
```

Log in with a Personal Access Token that has `write:packages`:

```bash
echo YOUR_PAT | docker login ghcr.io -u deidron --password-stdin
docker push ghcr.io/deidron/k8s-lab:1.0.0
```

### 4.2 Tags are never reused

A tag that gets overwritten on every build is the main source of "which version
is in production right now" questions — and worse, a node that already holds
that tag never fetches the new image, so a rollout reports success while running
the previous build. CI avoids this by publishing under the release tag and the
commit SHA, and the `tag-protection` ruleset stops a `v*` tag being moved after
the fact. Do not use `latest` in production: you cannot roll back to it.

### 4.3 The package is private by default

A new GHCR package is private even when the repository is public — this catches
people out, because the repository being public suggests the image is too. The
simplest fix is to make the package public in its settings.

Otherwise the cluster needs a pull secret:

```bash
kubectl create secret docker-registry ghcr \
  --docker-server=ghcr.io --docker-username=deidron --docker-password=YOUR_PAT
```

and `imagePullSecrets: [{name: ghcr}]` in the manifest. For a learning project
it is simpler to make the package public in the GitHub settings — then no secret
is needed.

---

## Phase 5. The production manifest

The dev overlays leave out three things production needs: probes (without them
Kubernetes will not notice a hung pod and keeps sending it traffic), resource
limits (one service can eat all memory and take the node down with it), and an
image from a registry rather than a locally built one.

All of that is already assembled in the `k8s/overlays/prod` overlay — no extra
file to create. The one value to fill in is `newTag` in `kustomization.yaml`:
the SHA of the commit whose image you want to run. It ships as a placeholder on
purpose, so a forgotten edit fails loudly instead of pulling something
unintended.

What the overlay adds on top of the base:

- **the registry image** in place of the locally built one, substituted by the
  `images:` transformer, so no manifest is edited by hand
- **a rolling update with `maxUnavailable: 0`** — a new pod becomes ready before
  an old one is removed, so the service never dips below full capacity
- **readiness and liveness probes**
- **resource requests and a memory limit**
- **`DEPLOY_ENV: prod`**, which surfaces as the `env` attribute on traces

Nothing else changes: the Service stays ClusterIP and the port stays named
`http`. Render it to see the exact result:

```bash
kubectl kustomize k8s/overlays/prod
```

About `limits.cpu`: deliberately unset. A CPU limit in Kubernetes works through
throttling and often causes unexpected latency in .NET applications; `requests`
is enough for the scheduler. A memory limit, by contrast, is needed — without it
the OOM killer picks the victim itself.

### 5.1 The health endpoint

Both probes point at `/healthz`, a route that returns 200 and does nothing else:

```csharp
app.MapGet("/healthz", static () => Results.Ok());
```

Pointing probes at a business route would be a mistake here. A check runs every
few seconds around the clock, so `/api/info` would emit a log line each time and
inflate the request metrics with traffic nobody asked for. `/healthz` writes no
log, is excluded from tracing along with `/metrics`, and is filtered out of the
RED dashboard queries.

It is deliberately shallow: it answers "the process is up and serving HTTP", not
"the dependencies are healthy". Once there is a database or a queue, a readiness
check that verifies them belongs in a separate endpoint — a liveness probe that
fails on a database blip would restart a perfectly good pod.

---

## Phase 6. Deploy and verify

```bash
kubectl apply -k k8s/overlays/prod
kubectl rollout status deployment/k8s-lab-deployment
```

Check from inside the cluster:

```bash
kubectl run curl --rm -it --image=curlimages/curl --restart=Never -- curl -s http://k8s-lab-service/api/info
```

Expected response:

```json
{"env":"prod","node_name":"k8s-prod","pod_name":"k8s-lab-..."}
```

### 6.1 Access from the local network

k3s ships servicelb, so `type: LoadBalancer` genuinely works here without any
intermediary: the service takes a port on the node IP and is reachable at
`192.168.1.50`. The alternative is an Ingress through the built-in Traefik,
which is preferable once there is more than one service.

**Phase check:** `http://192.168.1.50/api/info` opens from any machine on the
network, and `kubectl get pods` shows both replicas `Running` and `READY 1/1`.

---

## Phase 7. External access (optional)

Only if the service needs to be reachable from the internet. Because of CGNAT,
only an outbound tunnel works.

You need a domain registered in Cloudflare (~$10/year). In the Cloudflare Zero
Trust panel you create a tunnel, get a token, and then run `cloudflared` in the
cluster as an ordinary Deployment with that token in a secret. The route in the
panel points at the internal service address.

The router plays no part in this at all: no port forwarding, no KeenDNS, no
dependency on the ISP. The home IP is never exposed.

---

## Phase 8. Additional nodes (optional)

This gives no fault tolerance on a single physical host — if the host goes down,
everything goes down. But you do get real cluster behaviour: the scheduler
spreads replicas, and `kubectl drain` and zero-downtime upgrades start working.

The token from the first node:

```bash
sudo cat /var/lib/rancher/k3s/server/node-token
```

Clone the VM, change the hostname and IP, and **make sure** to reset the
machine-id — otherwise the clone conflicts with the original:

```bash
sudo rm -f /etc/machine-id && sudo systemd-machine-id-setup
```

Joining an agent:

```bash
curl -sfL https://get.k3s.io | K3S_URL=https://192.168.1.50:6443 K3S_TOKEN=TOKEN sh -
```

A sensible layout is one server and two agent nodes: workloads are genuinely
distributed while using half the memory of three server nodes with etcd.

---

## Phase 9. Operations

**Backups.** The cluster state lives in `/var/lib/rancher/k3s/server/db`. Also
keep the manifests in git: then the cluster is restored from source, not from a
snapshot. Before a k3s upgrade, take a manual Hyper-V checkpoint — a one-minute
rollback:

```powershell
Checkpoint-VM -Name k8s-prod -SnapshotName "before-k3s-upgrade"
```

**Upgrading k3s** — rerun the same install script.

**Logs and diagnostics:**

```bash
sudo journalctl -u k3s -f
kubectl get events --sort-by=.lastTimestamp
kubectl logs -l app=k8s-lab --tail=100
```

**Observability.** Storage stays outside the cluster — Loki, Prometheus, Tempo
and Grafana run in docker compose on the 24/7 host, and only the Alloy collector
lives in the cluster. Full description and rationale in
[../monitoring/README.md](../monitoring/README.md). Moving it over means
changing the host address in the Alloy config and in the `OTLP_ENDPOINT`
variable; everything else carries over unchanged.

The reason for keeping storage outside is simple: monitoring should not depend
on the thing it monitors. On a single node this has already proven itself — the
cluster disappeared once and the stand kept the whole history.

---

## Order and timing

| Phase | What you get | Time |
|------|--------------|-------|
| 1–2 | A VM with Ubuntu and a fixed IP | ~40 min |
| 3 | A working k3s cluster | ~10 min |
| 4 | The image in a registry | ~20 min |
| 5–6 | The service running on the local network | ~30 min |
| 7 | Access from the internet | ~40 min |
| 8 | A multi-node cluster | ~30 min |
| 9 | Backups and observability | ~40 min |

Phases 1–6 are worth doing in one sitting: the result is a working setup you can
come back to piece by piece.
