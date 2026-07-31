# Step 9 — Infrastructure Security Hardening

**Scope:** `deploy/` only. `src/`, `frontend/`, `sdk/` and `tests/` untouched — the 1198-passing
baseline from step 8 is unaffected by this step. Nothing committed.

**Validation:** `helm lint` and `helm template` pass for both environments and for every gated
combination; every rendered manifest also passes `kubectl apply --dry-run=server` against the live
k3s 1.34 API. Full output in [§7](#7-validation-output).

---

## 0. The finding that changed the design

`deploy.sh:22` sets `NAMESPACE=default`. On the live cluster that namespace also holds **nine
unrelated pods** — a whole second application:

```
rediensiam, rediensiam-postgres, rediensiam-dragonfly, rediensiam-hydra,
rediensiam-hydra-maester, rediensiam-keto,
yandee-client, yandee-essai, yandee-gateway-svc, yandee-layout-svc,
yandee-products-svc, yandee-superadmin, yandee-tenant-svc, yandee-vitrine
```

This has two consequences that shaped everything below.

1. **The baseline default-deny could not be namespace-wide.** A `podSelector: {}` deny in `default`
   would have cut off six yandee services on the next `deploy.sh` run. The policy is therefore
   **release-scoped** (`app.kubernetes.io/instance: rediensiam`). It closes the actual gap the
   audit named — a pod of this release with no policy is unrestricted — without reaching outside
   the release. The namespace-wide version is a one-line change *after* the release moves to its
   own namespace; that move is in the runbook (§6.1) and is the single highest-value item left.
2. **The `default`-namespace ingress rule was not purely a mistake.** `yandee-gateway-svc` calls
   RediensIAM's introspection endpoint pod-to-pod. Deleting the rule outright — which is what
   "the policy grants what it means to restrict" reads as — would have broken it. The rule is
   kept for **:5000 only**; the :5001 half, which was the actual finding, is gone.

Also found while reading the policy as a specification, and fixed:

> **The app's egress to Hydra `:4444` was never allowed.** `network-policies.yaml:22-26` permitted
> only `:4445`. `SystemHealthController.cs:126` probes `{HydraPublicUrl}/health/alive`, i.e. 4444.
> That probe has been failing at the network layer, silently, for as long as the policy has existed.
> Now allowed explicitly.

---

## 1. Per-finding: what changed and why

### R-16 (8.7) — unauthenticated cleartext registry + `pullPolicy: Always`

Two independent changes, because the finding is two problems.

**The registry no longer listens off-host.** `deploy/deploy.sh` — `REGISTRY_BIND=127.0.0.1`, and
`docker run -d -p ${REGISTRY_BIND}:5000:5000`. An existing container bound to `0.0.0.0` is detected
and recreated:

```bash
registry_bind_of() {
  docker inspect -f '{{ range $p, $c := .HostConfig.PortBindings }}{{ range $c }}{{ .HostIp }}{{ end }}{{ end }}' registry
}
```

The images live in the named volume `registry-data`, so the recreate is not destructive. Single-node
k3s pulls from `localhost:5000` through the node's own loopback, so this does not change how
containerd reaches it. **It does break a multi-node or VM-hosted k3s** — see §6.2.

**The deployment is pinned by digest, not by tag.** `values.yaml` gains `image.digest`;
`deployment.yaml` renders `repository@digest` when set and falls back to `repository:tag` when not.
`deploy.sh` resolves the digest from the push and refuses to continue without it:

```bash
IMAGE_DIGEST=$(docker inspect --format='{{index .RepoDigests 0}}' "${IMAGE}" | cut -d'@' -f2)
if [ -z "${IMAGE_DIGEST}" ]; then
  echo "  ERROR: could not resolve image digest after push — refusing to deploy a mutable tag"
  exit 1
fi
```

Both branches now pass `--set rediensiam.image.digest=…` and `--set …pullPolicy=IfNotPresent`.
`IfNotPresent` is only safe *because* of the digest — the two changes are one change.

**What this does not do:** no registry authentication, no registry TLS, no signature verification.
Loopback binding removes the network attacker; it does not remove a local one. §6.2 states the cost.

### R-02 (7.4) — auth surface on cleartext HTTP

`templates/ingress.yaml` rewritten. When `ingress.public.tls.enabled`:

- a `tls:` block with `secretName` defaulting to `<release>-public-tls`;
- `cert-manager.io/cluster-issuer` from `ingress.public.tls.clusterIssuer`;
- a Traefik `redirectScheme` middleware (`scheme: https, permanent: true`) attached to the router.

The router keeps `web,websecure` rather than dropping `web`. Traefik's shared redirect handler is a
no-op when the rewritten URL equals the original, so :80 redirects and :443 passes through — one
router, no loop, and port 80 stays available for the ACME HTTP-01 challenge.

`values.prod.yaml` sets `tls.enabled: true`. **`values.dev.yaml` sets it to `false` and says why:**
`iam.localhost` cannot be certified by any CA, and forcing TLS there would make Traefik serve its
own default self-signed cert on the login flow. This is the one place R-02 is not fixed, and it is
gated to dev specifically so prod cannot inherit it.

### R-05 (6.4) — admin port on a NodePort, escalated by chain C-7

`service.admin.type` is a value, **`ClusterIP` by default**. `nodePort:` renders only when the type
is `NodePort`. `values.prod.yaml` pins `ClusterIP` explicitly; `values.dev.yaml` opts back into
`NodePort` with the reasoning in-line.

This is the C-7 sequencing the frontend step asked for: step 6 made the console reachable, and this
step takes it off every node interface in the deployment path. In prod the admin surface is now
reachable **only** through the Tailscale-only admin ingress.

The NetworkPolicy follows: `:5001` is admitted from the ingress-controller namespace only. In dev,
where the NodePort exists, an unscoped `- ports: [{port: 5001}]` rule is rendered — a NodePort is
entered from the node's network stack, which no pod- or namespace-selector can match, so there is
no way to scope it while the NodePort exists. That rule is conditional on
`service.admin.type == NodePort` and cannot render in prod.

The self-signed cert is **not fixed** — `ingress.admin.clusterIssuer` is now a value so an operator
can point it somewhere real, but the default is still `selfsigned`. §6.3 gives the two ways out.

### R-15 (5.2) — Postgres `sslmode=disable`

Chart support implemented, **gated off**, because turning it on has prerequisites this chart cannot
satisfy on its own.

`postgres.local.tls.enabled` renders a cert-manager `Certificate` signed by the `selfsigned`
ClusterIssuer, mounts it at `/etc/postgres-tls`, and starts Postgres with
`-c ssl=on -c ssl_cert_file=… -c ssl_key_file=…`. The key mounts `0640` under `fsGroup: 70`, which
is the only mode Postgres accepts for a key it does not own.

Why it is off by default rather than on in prod: it requires cert-manager (this chart does not
install it), and a missing Certificate secret means the pod hangs on volume mount — a hard prod
outage for a 5.2. `deploy.sh`'s prod secret generator still writes `sslmode=disable`, with a comment
saying exactly what has to change and in what order. **Raising the sslmode before the server side is
enabled fails closed and the app will not connect**, so the ordering matters.

The honest ceiling with a selfSigned issuer is `sslmode=require` — encryption, no server
authentication. `verify-full` needs a real CA whose root is distributed to app, Hydra *and* Keto.
§6.4 costs both.

### R-19 (3.1) — prod CORS allowlists `http://localhost:30501`

Removed from `values.prod.yaml`. It could be removed cleanly *because* R-05 was fixed in the same
change: the origin existed for an SSH-tunnel fallback to the NodePort, and there is no NodePort in
prod any more. Chain C-9 closes with it.

### The `100.64.0.0/10` gap

`networkPolicy.privateRanges` in `values.yaml` is now the single source for both egress exception
lists, and contains `100.64.0.0/10` (Tailscale CGNAT) and `127.0.0.0/8` alongside the original four.
Rendered:

```yaml
except: ["10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","169.254.0.0/16","100.64.0.0/10","127.0.0.0/8"]
```

Step 5 made the application refuse the range; the pod can no longer route to it either. This is the
network half of R-10 and it applies to both the SMTP ports and the :443 egress.

### `network-policies.yaml:49-53` admitting the `default` namespace to :5001

Split, per §0. The in-namespace grant survives for **:5000 only**, expressed as
`from: [{podSelector: {}}]` — "all pods in this namespace" — which is namespace-agnostic and does not
hard-code `default`. The assumption is written into the manifest: in-cluster relying parties call
`/api/introspect` pod-to-pod. :5001 is no longer part of any namespace-wide grant.

### No baseline default-deny

Added, release-scoped (§0):

```yaml
kind: NetworkPolicy
metadata: {name: rediensiam-default-deny-ingress}
spec:
  podSelector:
    matchLabels: {app.kubernetes.io/instance: rediensiam}
  policyTypes: [Ingress]
```

`deployment.yaml` now stamps `app.kubernetes.io/instance` on the app pod template (added to
`template.metadata.labels`, **not** to the immutable `spec.selector`). The subchart pods already
carry it. The pod this newly closes is `rediensiam-hydra-maester`, which had no policy at all.

Verified safe with respect to kubelet: `rediensiam-hydra-lockdown` already restricts ingress to
Hydra and the pod is `1/1 Ready`, which is direct evidence that this CNI does not block node-sourced
probe traffic. Maester has no probes at all in any case.

**Egress is deliberately not blanket-denied.** Instead every component gained an explicit egress
policy, which achieves the same closure for those pods without a blanket rule:

| Pod | Egress now allowed |
|---|---|
| app | postgres 5432, dragonfly 6379, hydra 4444+4445, keto 4466+4467, kube-dns 53, SMTP 25/465/587/1025 and 443 minus `privateRanges` |
| hydra | postgres 5432, kube-dns 53 |
| keto | postgres 5432, kube-dns 53 |
| postgres | kube-dns 53 only |
| dragonfly | kube-dns 53 only |

Maester keeps unrestricted egress. It needs the kube-apiserver, and egress to a ClusterIP
apiserver is one of the things NetworkPolicy expresses badly; a wrong rule there fails silently.
Left as stated residual rather than half-done.

### Plaintext Dragonfly

`dragonfly.local.tls.enabled` renders a cert-manager Certificate and adds
`--tls --tls_cert_file --tls_key_file`, mounted `0440` under `fsGroup: 999`.

**Off by default and it must stay off until the connection string changes in the same step.** Unlike
Postgres, this is a hard cutover: with `--tls` Dragonfly stops answering cleartext, so the app loses
its cache the moment the flag lands unless `cacheUrl` already carries `ssl=true`. §6.5.

### Hydra `ttl.access_token` / `ttl.refresh_token` unset

Set in `values.yaml`:

```yaml
ttl:
  access_token: 15m
  refresh_token: 168h
  id_token: 15m
  auth_code: 10m
  login_consent_request: 30m
```

Rotation and reuse detection are Hydra defaults and are **not** overridden — step 8 established that
re-implementing them would be the second auth stack the brief warns against.

The reasoning behind the numbers is step 8 §3d's: `RevokeSessionsAsync` kills the consent session and
therefore the refresh chain, but an access token already minted survives to its own `exp`. So
`access_token: 15m` **is** the residual window after a revocation, down from Hydra's 1h default.
`refresh_token: 168h` is the outer bound on a stolen-but-unrotated chain; it costs an interactive
re-auth once a week and is the knob to raise if that is too aggressive.

**Validated against the real binary, not from memory** — the `ttl` block was fed to
`oryd/hydra:v25.4.0 serve all` and the process started and ran migrations, which means it passed
Hydra's config schema validation.

### Also fixed, cheap and in the same files

- **R-32 (3.1)** — `seccompProfile: {type: RuntimeDefault}` at pod level on the app and Dragonfly.
  Postgres already had it. CIS K8s 5.7.2.
- **R-07 (5.5)** — `umask 077` before `deploy.sh` writes `values.prod.secret.yaml`. It held the DB
  password, Hydra system secret, TOTP key and bootstrap admin password at `-rw-r--r--`.
- **DNS egress** narrowed from `:53 to anywhere` — the standard exfiltration channel — to the
  kube-dns pods, via a `rediensiam.dnsEgress` helper in `_helpers.tpl`. Verified against the live
  cluster: coredns carries `k8s-app: kube-dns` in `kube-system`.
- **Smoke-test honesty.** `deploy.sh`'s admin check now runs only when the service is a NodePort. In
  prod, ClusterIP + the policy means a `curl` from the operator's shell is *supposed* to be refused;
  it prints an explanation instead of a `✗`. A false failure at 3am is worse than no check.

---

## 2. Step-9 themes, judged against a k3s cluster

The brief asked for WAF, micro-segmentation, IDS/IPS and rate limiting. Two fit; two do not.

### Rate limiting / DDoS at the ingress — **implemented**

Traefik's CRDs ship with k3s (`middlewares.traefik.io` confirmed present on the cluster), so this
costs no new component. `templates/ingress.yaml` renders two middlewares onto the public router:

| Middleware | Config | Purpose |
|---|---|---|
| `<release>-ratelimit` | `average: 50`, `burst: 100`, `period: 1s` | per source IP |
| `<release>-bodylimit` | `maxRequestBodyBytes: 1048576` | 413 before a pod wakes |

Keyed on source IP by default, deliberately: the app's own `LoginRateLimiter` is per-account, so the
two layers key on different things instead of duplicating each other. This one bounds credential
stuffing and cheap L7 floods; the app's one bounds targeted account attacks. Both values are in
`values.yaml` and `rateLimit.enabled: false` turns it off.

**Limit worth stating:** this is L7 rate limiting on one node. It does nothing against a volumetric
L3/L4 flood — that needs something upstream of the node, which this deployment does not have.

### Micro-segmentation — **implemented**, and it is most of this step

The NetworkPolicy set in §3 is the micro-segmentation. Every pod in the release now has both an
ingress and an egress policy, the two unauthenticated ports have named single-source rules, and
egress is an allowlist rather than an exception list.

### WAF — **not installed, and here is why**

Traefik has no built-in WAF. The real option is the Coraza (OWASP CRS) plugin, and installing it
requires editing Traefik's **static** configuration — `experimental.plugins` in the k3s
HelmChartConfig — which is a cluster-level change outside this chart, plus a CRS ruleset to tune and
a false-positive budget on an auth surface that legitimately posts unusual strings. Half-installing
it would produce a chart that renders a middleware reference to a plugin that is not loaded, and
Traefik answers **503 on the whole router** when a referenced middleware does not resolve. That is a
total outage caused by a security control. §6.6 states what it would take.

What is in place instead is the cheap, non-breaking subset: the body cap above, plus the security
headers and CSP that step 6 put in the application.

### IDS/IPS — **not installed**

Nothing in k3s provides this. The realistic option is Falco as a privileged DaemonSet with either a
kernel module or eBPF probe, plus somewhere to send the alerts and someone to read them. It is a
genuine addition — a privileged workload on every node — and adding it silently would be worse than
not adding it. §6.7 states the cost.

---

## 3. The final NetworkPolicy set, and what each rule assumes

Six policies (five when `postgres.local.enabled` and `dragonfly.local.enabled` are false).

| # | Policy | Selects | Types |
|---|---|---|---|
| 1 | `<r>-default-deny-ingress` | `app.kubernetes.io/instance: <r>` | Ingress |
| 2 | `<r>-egress` | `app: <r>` | Ingress, Egress |
| 3 | `<r>-hydra-lockdown` | `app.kubernetes.io/name: hydra` | Ingress, Egress |
| 4 | `<r>-keto-lockdown` | `app.kubernetes.io/name: keto` | Ingress, Egress |
| 5 | `<r>-postgres-lockdown` | `app: <r>-postgres` | Ingress, Egress |
| 6 | `<r>-dragonfly-lockdown` | `app: <r>-dragonfly` | Ingress, Egress |

**Assumptions, stated so the next reader can check them rather than infer them:**

1. **The CNI enforces NetworkPolicy.** k3s ships flannel + the kube-router policy controller, so it
   does here. This is still the load-bearing assumption for the whole set — and specifically for
   Hydra `:4445` and Keto `:4467`, which have no authentication at all. If the CNI is ever swapped
   for one without a policy controller, these ports become open to the cluster and nothing warns you.
2. **The ingress controller lives in the namespace named by
   `networkPolicy.ingressControllerNamespace`** (default `kube-system`, correct for k3s Traefik).
   Rules keyed on it: app :5000, app :5001, hydra :4444.
3. **Pods in the release namespace are trusted to reach the app's public API on :5000.** This is
   what keeps in-cluster relying parties working. It is a real trust grant and it is namespace-wide
   — the mitigation is a dedicated namespace (§6.1), not a tighter selector, because the consumer
   set is not knowable from this repository.
4. **coredns carries `k8s-app: kube-dns` in `networkPolicy.dnsNamespace`.** Verified live. If DNS
   breaks after an upgrade this selector is the first thing to check; the helper carries that note.
5. **Only the app pod may reach Hydra :4445 and Keto :4466/:4467.** Unchanged from before and still
   the entire control on those ports. Anything that acquires the label `app: <release>` inherits the
   ability to write `System:rediensiam#super_admin`. Workload identity (a mesh) is the structural
   answer — architecture review §5.3 phase 2 — and is not in this step.
6. **In dev only, :5001 is open to anything that can route to the node.** Explicit, gated on
   `service.admin.type == NodePort`, and unreachable in prod.
7. **Maester is exempt from egress restriction.** Stated residual, see §1.

---

## 4. TLS and certificates, end to end

| Hop | Before | After |
|---|---|---|
| browser → public auth surface | cleartext :80 live | **prod:** TLS + 301 redirect from :80. **dev:** unchanged cleartext, gated |
| browser → admin console | NodePort :30501 cleartext, plus a self-signed ingress | **prod:** self-signed ingress only, NodePort gone. **dev:** NodePort |
| app → Hydra :4445 | none | none — NetworkPolicy is still the whole control |
| app → Keto :4466/:4467 | none | none — same |
| app/Hydra/Keto → Postgres | cleartext | chart support, gated off (§6.4) |
| app → Dragonfly | cleartext | chart support, gated off (§6.5) |

**Certificate sources, and what each is worth:**

- **`selfsigned` ClusterIssuer** — rendered by the chart whenever the admin ingress or either
  datastore TLS gate is on. Encryption without authentication. Adequate for a pod-to-pod hop already
  constrained by NetworkPolicy; **not** adequate for the admin console, where its practical effect is
  training operators to click through a certificate warning on the most privileged UI in the system.
- **ACME / Let's Encrypt** — opt-in, `certManager.acme.enabled` + `email`, renders a ClusterIssuer
  with an HTTP-01 solver. The chart `fail`s at template time if enabled without an email rather than
  producing an invalid issuer. This is what makes prod's `tls.enabled: true` actually resolve.
- **cert-manager itself is not installed by this chart.** Both of the above assume it is present.
  The admin ingress already assumed this before this step; the assumption is now written down.

---

## 5. Files changed

```
deploy/deploy.sh                                    registry bind, digest pin, umask, smoke test
deploy/rediensiam/values.yaml                       digest, admin type, TLS, middlewares,
                                                    certManager, networkPolicy, pg/df TLS, hydra ttl
deploy/rediensiam/values.dev.yaml                   NodePort opt-in, TLS off (gated)
deploy/rediensiam/values.prod.yaml                  ClusterIP admin, public TLS, R-19 CORS removal
deploy/rediensiam/templates/network-policies.yaml   rewritten
deploy/rediensiam/templates/ingress.yaml            rewritten — TLS + 3 middlewares
deploy/rediensiam/templates/_helpers.tpl            rediensiam.dnsEgress
deploy/rediensiam/templates/deployment.yaml         digest, seccomp, instance label
deploy/rediensiam/templates/service.yaml            admin type gate
deploy/rediensiam/templates/postgres.yaml           TLS gate + Certificate
deploy/rediensiam/templates/dragonfly.yaml          TLS gate + Certificate, seccomp
deploy/rediensiam/templates/cert-manager-issuer.yaml  ACME issuer, selfsigned condition widened
deploy/rediensiam/templates/admin-ingress.yaml      issuer configurable
```

Nothing under `src/`, `frontend/`, `sdk/` or `tests/`. No secret value was added to or changed in the
chart. **R-06 (hard-coded dev credentials) confirmed still present and left for step 10:**
`values.secret.yaml` carries `Password=changeme`, `bootstrapPassword: "Admin1234!"`, a literal
`encryptionKey`, and `CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`.

---

## 6. Runbook — what still requires operator action

### 6.1 Move the release to its own namespace *(highest value remaining)*

`default` holds nine unrelated pods, which is why the default-deny is release-scoped and why the
:5000 in-namespace grant is namespace-wide. Both tighten for free once the release is alone.

```bash
# 1. change deploy.sh:  NAMESPACE=rediensiam
# 2. kubectl create namespace rediensiam
# 3. label it so the yandee side can still be selected explicitly if it needs :5000:
#    kubectl label ns rediensiam kubernetes.io/metadata.name=rediensiam   # automatic on 1.21+
# 4. ./deploy/deploy.sh --dev
# 5. then, in network-policies.yaml, change the default-deny selector to podSelector: {}
#    and add policyTypes: [Ingress, Egress] with explicit allows.
```

Cross-namespace consumers of `/api/introspect` then need an explicit `namespaceSelector` rule —
which is the point: the grant becomes named instead of ambient.

### 6.2 Registry: authentication, TLS, and the multi-node caveat

Loopback binding assumes **k3s runs on the same host as the registry container**. If k3s is in a VM,
on another node, or reached over a network, `127.0.0.1:5000` is unreachable from containerd and pulls
will fail. In that case, revert `REGISTRY_BIND` to the interface k3s can reach *and* add the
authentication below in the same change — do not do one without the other.

Full fix, roughly two hours:

```bash
# 1. htpasswd + TLS on the registry
htpasswd -Bc /srv/registry/htpasswd ci
openssl req -newkey rsa:4096 -nodes -sha256 -x509 -days 825 \
  -keyout /srv/registry/tls.key -out /srv/registry/tls.crt -subj "/CN=registry.local"
docker run -d --name registry --restart=always -p 127.0.0.1:5000:5000 \
  -v registry-data:/var/lib/registry -v /srv/registry:/certs \
  -e REGISTRY_AUTH=htpasswd -e REGISTRY_AUTH_HTPASSWD_REALM=Registry \
  -e REGISTRY_AUTH_HTPASSWD_PATH=/certs/htpasswd \
  -e REGISTRY_HTTP_TLS_CERTIFICATE=/certs/tls.crt \
  -e REGISTRY_HTTP_TLS_KEY=/certs/tls.key registry:2

# 2. teach k3s the credentials and the CA
sudo tee /etc/rancher/k3s/registries.yaml <<'EOF'
configs:
  "registry.local:5000":
    auth: { username: ci, password: <password> }
    tls:  { ca_file: /srv/registry/tls.crt }
EOF
sudo systemctl restart k3s
```

Signature verification (cosign + an admission policy such as Kyverno or Connaissance) is a further
half-day and is the only thing that closes C-3 properly. Digest pinning, which is done, means an
attacker must now compromise the build host rather than answer for the registry — a real reduction,
not a closure.

### 6.3 A real certificate for the admin console

The `selfsigned` default remains. Two ways out, both operator-side:

- **Internal CA.** Create a cert-manager `CA` ClusterIssuer from your own root, set
  `ingress.admin.clusterIssuer` to it, and distribute the root to operator devices. This is the
  option that actually stops the click-through habit.
- **ACME DNS-01 for `ts.rediens.net`.** HTTP-01 cannot work — the name only resolves inside the
  Tailscale mesh. Needs a DNS-01 solver with credentials for the zone. Then set
  `ingress.admin.clusterIssuer: letsencrypt` and add the solver to the issuer in
  `cert-manager-issuer.yaml`.

Until one is done, treat the browser warning as a known defect, not as normal.

### 6.4 Postgres TLS (R-15), in order

```bash
# prerequisite: cert-manager installed
helm upgrade … --set rediensiam.postgres.local.tls.enabled=true      # server offers TLS; clients unaffected
# verify:
kubectl exec rediensiam-postgres-0 -- psql -U iam -c "show ssl;"     # expect: on
# only then, in values.prod.secret.yaml, raise all three DSNs:
#   hydra/keto: postgres://…?sslmode=require
#   app:        …;SSL Mode=Require;Trust Server Certificate=true
helm upgrade …
```

Order matters: `require` against a server without TLS fails to connect. `verify-full` is a further
step — it needs a CA issuer rather than the selfSigned one, the CA root mounted into the app, Hydra
and Keto pods (the Ory subcharts expose `extraVolumes`/`extraVolumeMounts` for this), and
`sslrootcert=` in each DSN. Roughly half a day, and it is what actually authenticates the server
rather than only encrypting to it.

### 6.5 Dragonfly TLS — one atomic change

```bash
# edit values.secret.yaml FIRST:  cacheUrl: "rediensiam-dragonfly:6379,ssl=true,abortConnect=false"
# then, in the SAME helm upgrade:
helm upgrade … --set rediensiam.dragonfly.local.tls.enabled=true
```

Splitting these across two deploys takes the cache offline in whichever order you split them.
StackExchange.Redis validates the certificate by default; with the selfSigned issuer you will also
need `sslProtocols`/trust handling or a CA issuer. Budget an hour and do it in dev first.

### 6.6 WAF

Coraza + OWASP CRS as a Traefik plugin. Cluster-level, not chart-level:

```yaml
# /var/lib/rancher/k3s/server/manifests/traefik-config.yaml
apiVersion: helm.cattle.io/v1
kind: HelmChartConfig
metadata: {name: traefik, namespace: kube-system}
spec:
  valuesContent: |-
    experimental:
      plugins:
        coraza:
          moduleName: github.com/jcchavezs/coraza-http-wasm-traefik
          version: v0.2.2
```

Then a `Middleware` with the CRS directives, attached to the public router. **Do not attach the
middleware before the plugin is loaded** — Traefik answers 503 for the entire router when a
referenced middleware does not resolve. Budget a day including false-positive tuning against the
login, register and consent flows, and run it in `DetectionOnly` first.

### 6.7 IDS/IPS

Falco, as a privileged DaemonSet:

```bash
helm repo add falcosecurity https://falcosecurity.github.io/charts
helm install falco falcosecurity/falco -n falco --create-namespace \
  --set driver.kind=modern_ebpf --set falcosidekick.enabled=true
```

This is a real addition to the cluster's attack surface — a privileged pod on every node — and it
produces alerts nobody reads unless a destination and an owner exist. Budget half a day for install
plus ongoing tuning. Recommended only once §6.1 and §6.2 are done; they are cheaper and close more.

### 6.8 Verify the CNI actually enforces policy

The single assumption everything else rests on. Two minutes:

```bash
kubectl run np-test --image=busybox --restart=Never -- sleep 3600
kubectl exec np-test -- wget -qO- --timeout=3 http://rediensiam-hydra-admin:4445/admin/clients
# expected: timeout. If it returns JSON, every NetworkPolicy in this chart is decorative
# and Hydra's admin API is open to the cluster.
kubectl delete pod np-test
```

---

## 7. Validation output

Actual output, not paraphrased.

```
$ helm lint rediensiam -f rediensiam/values.yaml -f rediensiam/values.dev.yaml -f rediensiam/values.secret.yaml
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed

$ helm lint rediensiam -f rediensiam/values.yaml -f rediensiam/values.prod.yaml -f <prod secrets>
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed
```

`helm template` — both environments render, 45 documents in dev and 48 in prod:

```
$ helm template rediensiam rediensiam -f values.yaml -f values.prod.yaml -f <prod secrets> | grep -E '^kind:' | sort | uniq -c
      1 kind: ClusterIssuer
      1 kind: ClusterRole
      1 kind: ClusterRoleBinding
      4 kind: ConfigMap
      5 kind: Deployment
      2 kind: Ingress
      3 kind: Middleware
      6 kind: NetworkPolicy
      2 kind: Pod
      1 kind: PodDisruptionBudget
      1 kind: Role
      1 kind: RoleBinding
      3 kind: Secret
     10 kind: Service
      6 kind: ServiceAccount
      1 kind: StatefulSet
```

Every gated combination renders (`postgres.local.tls.enabled=true`,
`dragonfly.local.tls.enabled=true`, `certManager.acme.enabled=true` + email,
`image.digest=sha256:…`) — `OK`, no error.

The guard fires as intended:

```
$ helm template … --set rediensiam.certManager.acme.enabled=true    # no email
Error: execution error at (rediensiam/templates/cert-manager-issuer.yaml:16:4):
rediensiam.certManager.acme.email is required when certManager.acme.enabled is true
```

Schema-validated against the live k3s 1.34 API — this is the check `helm lint` does not do, and it
is what confirms the Traefik CRD group is right:

```
$ kubectl apply --dry-run=server -f <rendered dev manifests>
networkpolicy.networking.k8s.io/rediensiam-default-deny-ingress created (server dry run)
networkpolicy.networking.k8s.io/rediensiam-egress configured (server dry run)
networkpolicy.networking.k8s.io/rediensiam-hydra-lockdown configured (server dry run)
networkpolicy.networking.k8s.io/rediensiam-keto-lockdown configured (server dry run)
networkpolicy.networking.k8s.io/rediensiam-postgres-lockdown configured (server dry run)
networkpolicy.networking.k8s.io/rediensiam-dragonfly-lockdown configured (server dry run)
middleware.traefik.io/rediensiam-ratelimit created (server dry run)
middleware.traefik.io/rediensiam-bodylimit created (server dry run)
ingress.networking.k8s.io/rediensiam-public configured (server dry run)
service/rediensiam-admin configured (server dry run)
…no errors; remaining output is last-applied-configuration warnings on pre-existing resources
```

`bash -n deploy/deploy.sh` — clean. (`shellcheck` is not installed on this machine, so the script
was not statically analysed beyond syntax.)

Hydra TTL config validated against the real binary:

```
$ docker run --rm -v ./hydra-test.yaml:/h.yaml:ro oryd/hydra:v25.4.0 serve all --dev -c /h.yaml
Thank you for using Ory Hydra v25.4.0!
… msg=Hydra is running migrations on every startup as DSN is memory.
… msg=> networks applied successfully
```

Startup past schema validation is the assertion — Hydra rejects unknown config keys.

---

## 8. What is left, with its cost

| Item | Why it is still open | Cost |
|---|---|---|
| Dedicated namespace → namespace-wide default-deny | `default` holds nine unrelated pods | 1h, §6.1 |
| Registry auth + TLS + signature verification | needs `registries.yaml` and an admission policy | 2h + 4h, §6.2 |
| Real admin certificate | needs a CA or a DNS-01 solver | 2–4h, §6.3 |
| Postgres TLS enabled (R-15) | needs cert-manager and a DSN change in the right order | 1h for `require`, +4h for `verify-full`, §6.4 |
| Dragonfly TLS enabled | hard cutover, must be atomic with the connection string | 1h, §6.5 |
| WAF | Traefik static config + CRS tuning | 1d, §6.6 |
| IDS/IPS | privileged DaemonSet, needs an alert owner | 0.5d + ongoing, §6.7 |
| Separate Postgres roles per component | architecture review §5.3's highest-leverage item; breaks C-4 | 0.5d, not attempted here |
| Service mesh / workload identity | the only real answer to unauthenticated `:4445` and `:4467` | multi-day, §5.3 phase 2 |
| Maester egress unrestricted | apiserver egress rules fail silently when wrong | left deliberately |
| Dev cleartext + dev NodePort | gated to dev on purpose; do not carry to a deployed node | by design |
| **Nothing was deployed or runtime-verified** | no `deploy.sh` run in this step | see below |

**The largest caveat.** Every claim here is template-, schema- and lint-verified. The chart was not
deployed. The three things a real `deploy.sh --dev` would confirm, and that reading cannot:

1. the narrowed DNS egress does not break name resolution for any pod;
2. the dev NodePort still reaches :5001 under the rewritten ingress rules;
3. the app's newly-permitted egress to Hydra :4444 makes `SystemHealthController`'s Hydra probe go
   green — it should have been failing before this change, and that is a falsifiable prediction.

Run `./deploy/deploy.sh --dev` and check those three before treating this step as done.
