# LitosAiAgent — Deploying `Litos.Api` to AWS

Evaluates whether `Litos.Api` (`ReadMe_HeadlessServiceTool.md`) — a single containerized ASP.NET
Core process combining a Blazor Server admin UI, a `BackgroundService` agent loop, and (per
`ReadMe_TelegramIntegrationTool.md`) the exclusive host for any chat-platform bridge — can run on
AWS instead of a home server/NAS, and if so, which AWS services fit it. Written before any
implementation; no code has changed as a result of this document. Builds directly on
`ReadMe_HeadlessServiceTool.md` (§5's architecture, §5.5's auth model, §5.6's filesystem
confinement, §9's open questions) rather than re-deriving any of it — this document answers one of
that doc's own open questions ("Reverse-proxy/HTTPS guidance... depends heavily on the individual
user's existing home-network setup") for the specific case where the answer is "there is no home
network; this runs on AWS instead."

**Research basis**: every AWS-specific claim below was verified against official AWS documentation
and pricing pages fetched directly for this document, not recalled from general knowledge — AWS's
container-hosting product lineup changes fast enough that a stale assumption here would be a real
mistake, not a cosmetic one. Citations are inline throughout.

## 1. What this is, in one sentence

Confirmed feasible: `Litos.Api`'s existing design (§2) maps cleanly onto **Amazon ECS running on
AWS Fargate**, fronted by an **Application Load Balancer** for HTTPS and WebSocket support, with
**Amazon EFS** providing the two persistent volume mounts `ReadMe_HeadlessServiceTool.md` §5.6
already specifies, and **AWS Systems Manager Parameter Store** supplying secrets — no code changes
to `Litos.Api` itself, only deployment configuration (§4).

## 2. Why this isn't a redesign — what AWS needs to satisfy, restated from the existing design

`ReadMe_HeadlessServiceTool.md` already fixed the shape this document has to fit into; nothing
here reopens those decisions:

- **One long-running container**, combining a Kestrel-hosted Blazor Server UI and an
  `AgentWorker : BackgroundService` in the same process (§5.2 there). Whatever AWS service is
  chosen has to run exactly one always-on container, not a request-scoped/serverless function
  model — `AgentWorker`'s loop and any in-flight `AgentLoop.RunTurnAsync` turn need a persistent
  process, not something that can be frozen between invocations.
- **A live SignalR circuit** for `Approvals.razor`'s real-time approval-list push (§5.1, §5.3
  there) — the hosting platform must support WebSocket/connection-upgrade traffic, not just plain
  request/response HTTP.
- **A multi-minute blocking wait is normal, not a failure mode** (§2 there: "`AgentLoop` has no
  timeout around tool execution... An approval that takes five minutes to click 'Approve' on a
  phone browser tab simply holds that one turn paused"). Critically, this blocking happens *inside
  `AgentWorker`'s own async wait on a `TaskCompletionSource`* (§5.3 there), not inside a single
  held-open HTTP request/response — so a platform's *HTTP request* timeout is not the thing to
  worry about; only a WebSocket/connection *idle* timeout could matter, and only for the SignalR
  circuit itself (§3.4).
- **Exactly two persistent volume mounts, deliberately confined, nothing else** (§5.6 there): a
  read/write `/workspace` the agent's file/shell tools operate against, and a separate `/data` for
  config/sessions/pending-approval state. This is a **hard requirement carried over unchanged** —
  AWS's job is to provide two mountable, persistent volumes into one container, not to redesign
  the confinement policy itself.
- **A single shared admin token today** (§5.5 there), explicitly reasoned around "the admin UI is
  reached over a private network or localhost, not the public internet." AWS deployment breaks
  that premise directly — §3.5 addresses this without redesigning the auth model itself (per the
  confirmed decision below).

## 3. Confirmed design decisions

| Decision | Choice | Rationale |
|---|---|---|
| Compute service | **Amazon ECS on AWS Fargate**, standard (non-Express Mode) configuration | See §3.1's elimination of the alternatives — this is the only option among those evaluated that satisfies WebSocket support, EFS mounting, and reasonable cost/ops burden for one always-on container simultaneously |
| Deployment tooling | **Standard ECS, not ECS Express Mode** | Express Mode (AWS's own recommended App Runner successor) cannot mount EFS — a hard blocker given §2's carried-over two-volume requirement. Noted as a fast path worth knowing about, not evaluated further, since it doesn't fit this design's stated needs |
| Persistent storage | **Amazon EFS**, one file system, two Access Points (`/workspace`, `/data`) | Native to ECS/Fargate via `EFSVolumeConfiguration`; EBS is instance-local and a poor fit since Fargate tasks aren't pinned to a specific host the way an EC2-attached EBS volume would need |
| Secrets | **AWS Systems Manager Parameter Store, Standard tier**, `SecureString` type | Free at this scale (a handful of small secrets, no rotation/cross-account need) versus Secrets Manager's per-secret monthly charge — see §3.3 for the exact trade-off and when Secrets Manager would become worth paying for |
| Network exposure / HTTPS | **Application Load Balancer + AWS Certificate Manager** | ALB is required anyway for WebSocket support (§2) and to front Fargate at all; ACM certs are free when attached to an ALB listener |
| Auth posture (v1) | **Keep §5.5's shared `ADMIN_TOKEN` model, paired with an ALB security-group IP allowlist** as the network-layer stopgap — not a redesign of app-level auth | Matches this whole design's established pattern (`ReadMe_TelegramIntegrationTool.md` §7, `ReadMe_HeadlessServiceTool.md`'s "ship minimal, harden later") of shipping the minimal defensible version and naming what's deferred, rather than blocking AWS deployment on building real per-user auth (OIDC/Cognito) first. See §3.5 for exactly what this does and doesn't protect against |
| State migration between home-server and AWS deployments | **Out of scope for this document** — noted as an open question (§6), not designed here | This document answers "can `Litos.Api` run on AWS," not "how do I move an existing home deployment's `~/.litos-docker` state onto EFS" — a smaller, separate concern (file copy into the EFS mount) that doesn't need dedicated design work in a first version |

### 3.1 Compute service — eliminating the alternatives

Four AWS options were evaluated directly against §2's requirements, not assumed:

- **AWS App Runner — eliminated on two independent grounds.** First, and decisively: *App Runner
  is closed to new customers* — AWS's own docs state plainly, "AWS App Runner is no longer open to
  new customers. Existing customers can continue to use the service as normal," and AWS's
  migration guidance for existing App Runner users points them at ECS Express Mode instead.
  Second, even setting that aside, App Runner fails two hard requirements on technical merit: no
  WebSocket support (an open, unresolved item on App Runner's own public GitHub roadmap) and no
  EFS/persistent-volume support (same roadmap, a separate open issue, corroborated by AWS's own
  community forum). Either gap alone would rule it out for this workload; the new-customer freeze
  makes the question moot regardless.
- **ECS Express Mode — the closest thing to App Runner's successor, still ruled out.** AWS's
  official guidance frames Express Mode as the replacement path for App Runner customers: one API
  call provisions the ECS service, ALB, target groups, and networking together, at no extra charge
  beyond the underlying resources. It would satisfy the WebSocket requirement (it's Fargate + ALB
  underneath) but **does not support EFS volumes** — EFS is explicitly listed among the standard
  reasons to "eject" from Express Mode into full manual ECS configuration. Since §2's two-mount
  requirement is non-negotiable (carried over from `ReadMe_HeadlessServiceTool.md` §5.6, not
  reopened here), Express Mode doesn't fit, and there's no remaining advantage to starting there
  only to eject immediately.
- **ECS on EC2 — technically workable, no advantage for this workload.** Would satisfy every
  functional requirement (WebSocket via the same ALB mechanism, EFS mounting works identically on
  EC2 launch type), but means paying for an EC2 instance around the clock regardless of the single
  task's actual utilization, plus owning AMI patching and container-agent updates — real ongoing
  operational burden AWS's own launch-type guidance frames as justified only for "specialized
  hardware requirements... capacity reservations... privileged capabilities or custom AMIs," none
  of which apply here.
- **Lightsail Containers — eliminated outright.** No block-storage/disk-attachment support for the
  Container service specifically (Lightsail's attached-disk feature is VM-instance-only) — fails
  §2's persistent-storage requirement immediately, no cost/complexity trade-off worth making since
  the functional requirement isn't met at all.

**Fargate cost shape, for scale**: $0.04048/vCPU-hour + $0.004445/GB-memory-hour in us-east-1,
billed per-second. A modest always-on task (e.g. 0.5 vCPU / 1 GB) works out to roughly $15–20/month
in raw compute, before the ALB (~$16–20/month base, hourly + usage-based) or EFS. This is a
feasibility-level estimate, not a quote — actual sizing depends on how much memory/CPU the LLM
provider SDKs and Blazor Server rendering actually need under real use, which this document doesn't
benchmark.

### 3.2 Persistent storage — EFS, mounted as two Access Points

One EFS file system, split into two Access Points so both of §5.6's mounts come from a single
managed resource rather than two separate file systems:

```
EFS file system: litos-api-state
├── Access Point "workspace" → mounted at /workspace in the container
└── Access Point "data"      → mounted at /data in the container
```

Mechanically: the ECS task definition's `volumes[].efsVolumeConfiguration` references the file
system ID (and, per Access Point, an `authorizationConfig.accessPointId` for a scoped root
directory plus IAM-based mount authorization); the container definition's `mountPoints` maps each
volume to its container path. This requires a security-group rule opening port 2049 (NFS) between
the Fargate task's security group and the EFS mount targets' security group — the one new network
rule this design didn't need on a home server. Fargate must run **platform version 1.4.0 or later
(Linux)** for EFS support, which is the current default for new task definitions and not a
meaningful constraint in practice.

**Cost**: EFS Standard (Regional/Multi-AZ) runs roughly $0.30/GB-month; One Zone Standard roughly
$0.16/GB-month, with a 12-month free tier of 5 GB for new AWS accounts. **One Zone is the
recommended tier here** — this design is explicitly single-tenant, non-HA (§7 of
`ReadMe_HeadlessServiceTool.md` defers even multi-*caller* isolation, let alone multi-AZ
resilience for one operator's personal instance), so paying double for Multi-AZ redundancy buys
little. At the "few MB to low single-digit GB" scale §5.6's workspace/state directories actually
need, this lands at low-single-digit dollars per month either way.

### 3.3 Secrets — Parameter Store Standard tier

AWS's own ECS guidance addresses this exact scenario directly: *"These secrets can be referenced...
as environment variables that use the `secrets` container definition parameter... Secrets Manager
[and] Parameter Store... are similar because they're both managed key-value stores that use AWS KMS
to encrypt sensitive data. Secrets Manager, however, also includes the ability to automatically
rotate secrets, generate random secrets, and share secrets across accounts. To utilize these
features, use Secrets Manager. Otherwise, use encrypted parameters in Systems Manager Parameter
Store."* This design has three small secrets to store — `ADMIN_TOKEN`, `TELEGRAM_BOT_TOKEN`, and
whichever LLM provider API key(s) are configured (`LitosConfig`'s existing env-var-first
resolution, unchanged) — with no stated need for rotation, cross-account sharing, or generated
secrets. **Parameter Store Standard tier** (`SecureString` type, KMS-encrypted) is free at this
scale — AWS's own tier-comparison doc lists "Cost: No additional charge" for Standard versus
"Charges apply" for Advanced — against Secrets Manager's $0.40/secret/month
(≈$1.20/month total for three secrets, plus negligible per-call cost). If a later revision wants
scheduled rotation for the LLM API key or admin token, that's the point at which paying for Secrets
Manager becomes worth it — not before.

Mechanically, this mirrors §5.5's existing design almost exactly: the ECS task definition's
`secrets` array references each Parameter Store parameter name; the ECS agent resolves and injects
it as a real environment variable at task start, read by `LitosConfig.GetApiKey(...)` exactly as it
already reads any other env var — **no code change to `LitosConfig` or its resolution order**, only
where the env var's value comes from at deploy time. One operational note worth stating plainly:
**secret rotation requires a forced new task deployment** — ECS does not hot-reload an
already-running task's environment variables if the underlying parameter changes, matching this
design's existing "config is read at startup" assumption throughout (`LitosConfig.Load()`,
`ReadMe_HeadlessServiceTool.md` §5.2).

### 3.4 Network exposure and HTTPS — ALB + ACM, and why the blocking-wait timeout concern doesn't apply

ALB + an AWS Certificate Manager certificate is the standard, low-ceremony way to front a Fargate
service with real HTTPS — ACM certs are free when attached to an ALB listener, and an ALB is
required anyway for WebSocket support and to front Fargate at all (§3.1), so this isn't an
additional piece of infrastructure beyond what §3.1 already requires.

**Addressing the obvious worry directly, since it's the natural but incorrect assumption to make**:
ALB's `idle_timeout` load-balancer attribute defaults to 60 seconds (configurable 1–4000 seconds)
and fires when a connection sits with *zero bytes flowing in either direction* for that duration —
it is not "the backend hasn't finished responding to this request yet." Per §2, the multi-minute
approval-wait lives inside `AgentWorker`'s own suspended `Task` (a `TaskCompletionSource` await),
not inside a single open HTTP request being held by Kestrel — so ALB's idle timeout is simply not
in that path at all. The one place an idle timeout *could* matter is the Blazor Server SignalR
circuit's persistent WebSocket connection, and Blazor Server's SignalR client already sends its own
periodic keepalive pings over that circuit by default, comfortably under the 60-second default
window. Raising `idle_timeout` to something like 120–300 seconds is still a cheap, free, reasonable
defensive move — but per this analysis it is not strictly required by the architecture as designed,
worth stating explicitly since "my approval-wait will get killed by a load-balancer timeout" is the
natural but mistaken assumption a reader would otherwise make.

### 3.5 Auth posture — keeping §5.5's shared token, adding a network-layer stopgap

Per the confirmed decision, this document does **not** redesign `ReadMe_HeadlessServiceTool.md`
§5.5's shared-`ADMIN_TOKEN` model — it pairs that existing model with an AWS-native network-layer
control that a home-server deployment had no equivalent for:

- **ALB security-group IP allowlist**: restrict the ALB's inbound security-group rule on port 443
  to the operator's known IP address(es)/range, instead of the usual `0.0.0.0/0`. AWS's own VPC
  security-group guidance directly endorses this pattern for exactly this kind of restricted-access
  scenario. This is free, requires no additional AWS service, and directly matches the actual
  threat model (one named operator, not a public audience) — the same "narrower blast radius by
  scope" reasoning `ReadMe_TelegramIntegrationTool.md` §10.4 already applied when comparing this
  design against OpenClaw's broader multi-tenant threat model.
- **What this does and doesn't protect against, stated with the same explicitness §5.6/§7 of the
  other two documents already use for their own controls**: an IP allowlist stops an
  unauthenticated internet-wide scanner from ever reaching the login prompt at all — a real,
  meaningful reduction versus a bare public ALB. It does **not** protect against the admin token
  itself leaking (§8 of `ReadMe_TelegramIntegrationTool.md`'s "a Telegram bot token is functionally
  a credential" reasoning applies identically to `ADMIN_TOKEN`), and it becomes a source of friction
  the moment the operator's own IP changes (mobile networks, travel, a new ISP-assigned address) —
  a real usability cost this document accepts rather than hides.
- **AWS Client VPN was considered and set aside as disproportionate for v1.** It would give a
  stronger, IP-independent guarantee (only VPN-connected clients can reach the ALB's security
  group at all) but carries real hourly/per-connection cost and setup ceremony for what is, per
  this design's own scope, one operator — the same "don't build for a threat model this design
  doesn't take on" reasoning already applied elsewhere. Worth revisiting if the operator's IP is
  highly dynamic enough that allowlist maintenance becomes genuinely painful, not adopted by
  default.
- **This remains a stopgap, not a solution — matching how `ReadMe_HeadlessServiceTool.md` §9
  already flagged full auth as an open question independent of AWS.** Real per-user
  authentication (AWS Cognito, or any OIDC provider swapped in ahead of `AdminTokenFilter`) is not
  designed here; the IP allowlist buys time, it doesn't replace that eventual work.

## 4. What changes in `Litos.Api` itself: nothing

Worth stating as plainly as `ReadMe_HeadlessServiceTool.md` §4 states the equivalent claim for
`Litos.Host`: **no code in `Litos.Api`, `Litos.Host`, `Litos.Agent`, `Litos.Tools`, or
`Litos.Console`/`Litos.Gui` needs to change for this deployment target.** Everything in §3 is
deployment configuration (an ECS task definition, an ALB listener, EFS Access Points, Parameter
Store entries) layered on top of the exact same Docker image `ReadMe_HeadlessServiceTool.md` §5.1's
`Dockerfile` already describes:

- `LitosConfig.Load()` already resolves every secret from environment variables first (§6.1 of
  `ReadMe_HeadlessServiceTool.md`) — Parameter Store injection via the ECS task definition's
  `secrets` field is invisible to this code path; it's still just an env var by the time
  `LitosConfig` reads it.
- `LITOS_WORKSPACE=/workspace` (§5.6 there) is satisfied identically whether `/workspace` is a
  local bind mount on a home server or an EFS Access Point mount on Fargate — the container
  filesystem view is what the code interacts with either way.
- `AgentWorker`'s `BackgroundService` lifecycle and graceful-shutdown handling via
  `IHostApplicationLifetime` (§5.2 there) responds to `SIGTERM` the same way whether it's sent by
  `docker stop` on a home box or an ECS task-stop event during a deployment/scale-in.

This is the same "third proof" pattern `ReadMe_HeadlessServiceTool.md` §2 already established for
`Litos.Host` across `Litos.Console`/`Litos.Gui`/`Litos.Api` — here it's a second proof that
`Litos.Api` itself, once built, is deployment-target-agnostic by construction, not because AWS
support was specifically designed in.

## 5. What's explicitly out of scope for this document

Named here so the boundary is a decision, matching the convention the other two documents already
use:

- **Migrating existing home-server state onto AWS's EFS volume** — noted as an open question (§6),
  not designed. Mechanically this is "copy the contents of `~/.litos-docker` into the EFS mount
  before first boot," but the actual steps (downtime, whether sessions mid-flight survive the
  move, whether a linked Telegram chat's `chatId → sessionId` mapping needs anything special) are
  not worked through here.
- **Autoscaling, multi-instance, or high-availability configuration** — this design is
  single-tenant, single-task, by the same reasoning §3.2 applied to choosing One Zone EFS over
  Multi-AZ. Running more than one task of the same `Litos.Api` service would immediately collide
  with `ReadMe_HeadlessServiceTool.md` §7's deferred multi-caller isolation work (§10.4 of the
  design doc) — two tasks sharing one EFS-mounted `/workspace` with no coordination is a correctness
  problem, not just a cost one.
- **CI/CD pipeline for building and pushing the container image to ECR** — assumed but not
  designed; any standard `docker build` → `ecr:PutImage` → ECS service update pipeline (GitHub
  Actions, CodePipeline, or manual) works, and this document doesn't pick one.
- **Cost optimization beyond the tier choices already named** (Fargate Spot, Savings Plans,
  Compute Savings) — feasibility-level detail only, matching §3 of `ReadMe_HeadlessServiceTool.md`'s
  own stated depth for Docker specifics.
- **A finished, tested Terraform/CloudFormation/CDK stack** — this document establishes the shape
  (§3), not deployable infrastructure-as-code.

## 6. Open questions

- **Home-server-to-AWS state migration** (§5): does this need dedicated tooling (an export/import
  command), or is "stop the home deployment, `aws efs` sync the state directory, start the AWS
  deployment" an acceptable manual process for a first version?
- **Multi-region/latency**: this document assumes a single AWS region chosen once at deployment
  time (matching the LLM provider API calls' own single-region-agnostic outbound HTTPS shape,
  `ReadMe_TelegramIntegrationTool.md` §4) — not evaluated for whether region choice matters for
  LLM-provider latency or Telegram Bot API reachability, likely a non-issue at this usage scale but
  unverified.
- **When does the IP-allowlist stopgap (§3.5) stop being adequate?** Named the same way
  `ReadMe_HeadlessServiceTool.md` §9 already named "when does full workspace isolation become
  necessary" — worth a similar explicit trigger condition in a future revision (e.g. "the moment a
  second named user needs their own login, not just network-level access") rather than left
  purely to judgment.
- **EFS performance mode/throughput mode defaults** — not evaluated here; the default General
  Purpose performance mode and Bursting throughput mode are almost certainly adequate for a "few MB
  to low GB" state directory with light read/write activity, but this document doesn't verify that
  against the specific I/O pattern `ShellTool`/`WriteFileTool` would generate under real use.
- **Logging/observability** (CloudWatch Logs via the `awslogs` log driver is the obvious default
  for an ECS task, matching how the container's stdout/stderr already work) — not designed here,
  noted as a natural but unaddressed next question.
