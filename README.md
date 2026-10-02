<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/banner.dark.svg">
  <img alt="Banner" src="assets/banner.light.svg">
</picture>

# GZ::CTF — Extended

An extended fork of **[GZ::CTF](https://github.com/GZTimeWalker/GZCTF)** by
[@GZTimeWalker](https://github.com/GZTimeWalker), adding shared-flag acceptance, Discord notifications, and a
container activity log for running and policing CTFs.

> **Credit / upstream:** This project is built on top of **GZ::CTF** — an open-source CTF platform based on
> ASP.NET Core. All of the base platform is the work of the GZCTF authors and contributors. Please star and
> support the original project: **https://github.com/GZTimeWalker/GZCTF**. For base platform documentation,
> see **https://gzctf.gzti.me/**. This fork only adds the three features documented below; it is not
> affiliated with or endorsed by the upstream maintainers.

---

## What this fork adds

1. **Accept shared (cheated) flags** — a team that submits another team's *dynamic* flag now gets the solve
   and points, while the cheat is still detected and recorded for admins.
2. **Discord notifications** — first-blood announcements (public channel) and cheat alerts (private
   channel), driven by an external `discord.yml`.
3. **Container activity log** — a per-challenge, per-team timeline of container lifecycle events and flag
   submissions, delivered to a private Discord channel (one thread per challenge) plus admin JSON/CSV
   endpoints, to spot suspiciously fast or shared-solution solves.

All three are **opt-in and fail-safe**: if `discord.yml` is absent, features 2 and 3 are silently disabled
and the app behaves like upstream. Feature 1 is always on (it changes how shared dynamic flags are scored)
but changes nothing for static challenges.

**New runtime dependency:** `YamlDotNet` 16.3.0 (central-managed in `src/Directory.Packages.props`), used to
parse `discord.yml`. **One new DB table**, `ActivityLogEntries` (migration `AddActivityLog`), applied
automatically at startup. No existing enums the frontend uses were changed; no frontend source changed.

---

## Table of contents
- [Quick start](#quick-start)
- [Feature 1 — Accept shared flags](#feature-1--accept-shared-flags)
- [Feature 2 — Discord notifications](#feature-2--discord-notifications)
- [Feature 3 — Container activity log](#feature-3--container-activity-log)
- [`discord.yml` full reference](#discordyml-full-reference)
- [Discord bot setup & permissions](#discord-bot-setup--permissions)
- [Admin API endpoints](#admin-api-endpoints)
- [Building & deploying a custom image](#building--deploying-a-custom-image)
- [Database migration](#database-migration)
- [Building & running the tests](#building--running-the-tests)
- [Working on the code (map of changes)](#working-on-the-code-map-of-changes)
- [Limitations & follow-ups](#limitations--follow-ups)
- [Credits & license](#credits--license)

---

## Quick start

```bash
# Prereqs: .NET 10 SDK (global.json pins 10.0.401), Node + pnpm (for the SPA), Docker.
export DOTNET_ROOT="$HOME/.dotnet" && export PATH="$HOME/.dotnet:$PATH"

# Build the backend
dotnet build src/GZCTF/GZCTF.csproj -c Debug

# Run the tests (integration tests need Docker for a Postgres Testcontainer)
dotnet test src/GZCTF.Test/GZCTF.Test.csproj                           # unit
dotnet test src/GZCTF.Integration.Test/GZCTF.Integration.Test.csproj   # integration
```

To enable Discord notifications and/or the activity log, copy `discord.example.yml` to `discord.yml`, fill in
the bot token and channel ids (see below), and mount it into the container at `/app/discord.yml`. Without
that file, only Feature 1 is active and everything else matches upstream.

---

## Feature 1 — Accept shared flags

**What changed.** Upstream treats a team submitting another team's dynamic flag as `WrongAnswer` (the cheat
is recorded, but no points). This fork **accepts** that submission — the submitting team gets the solve and
dynamic points — while still recording the cheat exactly as before (a `CheatInfo` row, a `CheatDetected`
game event, the cheat log line, and the admin-monitor SignalR push). Static challenges are unchanged. **A
cheated solve never earns or occupies a blood bonus.**

**Why it works.** The single source of truth for "this team solved it" is the `FirstSolve` table, which the
scoreboard reads — not `Submission.Status`. So the fix inserts a `FirstSolve` for an accepted-cheat solve
while leaving the stored submission status as `CheatDetected` (so admins still see it).

**How it behaves:**
- The submitting **player sees `Accepted`** (the `Status` endpoint maps `CheatDetected → Accepted`),
  identical to a legitimate solve — so the anti-cheat is silent to the cheater.
- **Admins** still see the `CheatDetected` submission, the `CheatDetected` event, and the `CheatInfo` record.
- The cheated solve **scores normally** (and counts toward dynamic-score decay) but is **excluded from
  blood** — the next legitimate solver still gets first/second/third blood.
- A cheated-but-accepted solve is identified by the pair (`FirstSolve.SubmissionId` + a `CheatInfo` with the
  same `SubmissionId`). **No new enum value and no new DB migration** were needed for this feature.

**Key code.** Cheat detection was folded into `GameInstanceRepository.VerifyAnswer` (inside the same
transaction + `pg_advisory_xact_lock`), replacing the old post-hoc `CheckCheat`. `FlagChecker` handles
`Accepted` and `CheatDetected` together. `GameController.Status` maps `CheatDetected → Accepted`.
`GameRepository.GenScoreboard` marks cheated solves `IsCheat` and forces `bloodEligible = false`.

---

## Feature 2 — Discord notifications

**What it posts:**
1. **First blood** for a challenge → a **public** channel (optionally also 2nd/3rd blood).
2. **Flag sharing / cheat detected** → a **private** (admin-only) channel.

**Design.** Everything is driven by `discord.yml`; if missing or disabled, the app runs exactly as before.
All Discord work runs **off the flag-checking hot path**: the hook points only enqueue onto a bounded
in-process channel (capacity 1000), drained by a background service that does the DB lookups and REST calls.
It can never slow down or break flag submission.

**Reliability & safety:**
- **No pings:** every message sets `allowed_mentions: { parse: [] }`.
- **Markdown-safe:** all user text (team/user names, challenge titles, flags) is escaped and truncated to
  Discord limits; the flag is wrapped in an inline code span and only included when `show_submitted_flag`.
- **Resilient sender:** honours 429 `retry_after`, retries 5xx/network with exponential backoff, drops after
  the final attempt, and **never throws** into the flag checker.
- **Token safety:** the bot token only sets the HTTP `Authorization` header; it is never logged.
- **REST only** (no gateway) — the bot never shows "online"; this is intentional.

**Hook correctness.** The first-blood hook sits in the exact block that creates the blood `GameNotice`, using
the same final `SubmissionType`, so the Discord message always agrees with the in-game notice. Because a
cheated solve is forced to `SubmissionType.Normal`, it can never produce a first-blood message. The cheat
hook fires once per detected submission.

---

## Feature 3 — Container activity log

An append-only, per-challenge, per-team timeline of **container lifecycle events** and **flag submissions**
for container challenges, delivered to a **private** Discord channel (one thread per challenge) plus admin
API endpoints. It helps spot suspiciously fast or shared-solution solves (e.g. a team that receives a solve
script, starts its own container, and submits its own correct flag within minutes). It reuses the Discord
integration's queue, rate-limited sender, escaping and `allowed_mentions` protection. Disabling it leaves
everything else working.

### What it records
A dedicated, append-only table `ActivityLogEntries` (never touches existing `GameEvent`s or the enums the
frontend uses) captures, per team per challenge:

| Entry type | When |
|---|---|
| `ContainerStarted` | container created for a team instance |
| `ContainerStartFailed` | create failed (config error / orchestrator failure) — *previously unlogged* |
| `ContainerExtended` | lifetime extended — *previously unlogged* |
| `ContainerDestroyedByUser` | player deleted the container |
| `ContainerAutoDestroyedAfterSolve` | user destroy when a solve already exists (best-effort) |
| `ContainerDestroyedOnLimit` | evicted on container-count limit |
| `ContainerExpired` | expiry cron — *previously unlogged* |
| `ContainerDestroyedByAdmin` | admin destroyed it — *previously unlogged* |
| `ContainerDestroyedOnChallengeRemoval` | challenge disabled/deleted |
| `FlagWrong` / `FlagAccepted` (+ blood tier) / `FlagCheatDetected` (+ source team) | flag submission result |

All container destroys are captured because they funnel through one choke point
(`ContainerRepository.DestroyContainer`), which now takes a `ContainerDestroyReason`.

### Delivery
- **One thread per challenge** named `#<challengeId> <title>`, created in the private activity channel. On
  startup/first use it finds an existing thread by name among **active and archived** threads (unarchiving a
  match) before creating one, so restarts don't duplicate threads.
- **Live feed:** new entries are batched per challenge every `batch_seconds` into one compact message per
  thread (one line per event: team, user, event, Discord timestamp). Messages over 2000 chars are split.
  **Flag values never appear in the live feed.**
- **Summary report:** a short embed (teams started, solves, flagged teams + reasons) plus the full timeline
  attached as a `.txt` (sorted by team id then time, in the configured timezone). Triggered on the periodic
  interval, at game end, and on demand via the admin endpoint.

### Suspicion rules (configurable)
Per solved team the report flags:
- **Fast solve** — solved within `fast_solve_minutes` of that team's most recent container start.
- **No wrong attempts** — solved with zero wrong submissions (if `no_wrong_attempts`).
- **Close solve** — solved within `close_solve_window_minutes` after another team's solve of the same
  challenge.

Per team it also computes total container uptime (a container still running at report time counts up to the
report time), number of starts, number of wrong submissions, and time-to-solve from first/latest start.

### Example timeline (`challenge-12-timeline.txt`)
```
Challenge #12 — Login Bypass (DynamicContainer) — Game: Example CTF 2026
Generated: 2026-10-20 15:00 (Asia/Dhaka)

Team #3 "AlphaSec"
  10:02:11  ▶ container started (user: alice)
  10:32:11  ⏹ container expired
  11:15:40  ▶ container started (user: bob)
  11:17:02  ✗ wrong flag (user: bob)
  11:18:30  ✓ solved — Normal (user: bob)
  ⚠ SUSPICIOUS: solved 2m50s after container start; solved 4m after Team #7
  Summary: total container uptime 1h12m, 2 starts, 1 wrong flags, solved after 1h16m
```

### Reliability & security
- Hooks only **enqueue** (lock-free `TryWrite`) onto a bounded channel (capacity 5000) and never throw; a
  full queue drops with a warning. Container start/stop and flag checking are never blocked.
- Resolution and persistence happen in a background service; `ActivityLogRepository.AddAsync` catches/logs
  any DB failure. The destroy hook captures the instance key **before** the row is removed and logs **after**
  the destroy commits, so a logging fault can never affect the destroy.
- Posted **only** to the private `activity_log.channel_id`; never to the public first-blood channel (a
  startup **warning** fires if the two ids match). The live feed never includes flags; submitted wrong flags
  appear only in the report `.txt` / CSV / JSON and only when `cheat_detection.show_submitted_flag` is true.

---

## `discord.yml` full reference

Copy `discord.example.yml` → `discord.yml`. Path resolves from `GZCTF_DISCORD_CONFIG` (default
`/app/discord.yml`). The file is git-ignored — never commit the real token. Config is read **once at
startup**; editing it requires a restart.

```yaml
enabled: true                 # master switch for the whole integration
bot_token: "YOUR_BOT_TOKEN"   # Discord bot token; kept secret, never logged

first_blood:
  enabled: true
  channel_id: "123456789012345678"   # PUBLIC channel id (numeric snowflake, as a string)
  include_second_third_blood: false  # also post 2nd/3rd blood. default false

cheat_detection:
  enabled: true
  channel_id: "234567890123456789"   # PRIVATE channel id
  show_submitted_flag: true          # include the shared flag in the private alert. default false

activity_log:
  enabled: true
  channel_id: "345678901234567890"      # PRIVATE channel; threads are created inside it
  challenge_types: ["DynamicContainer"] # add "StaticContainer" to also track those
  timezone: "Asia/Dhaka"                # IANA/Windows id; used for report times (default UTC)
  live_feed:
    enabled: true
    batch_seconds: 30                   # one message per challenge thread per interval (5–3600)
  summary:
    interval_minutes: 60                # periodic full report; 0 = disabled
    post_at_game_end: true
  suspicion:
    fast_solve_minutes: 10              # solve within N min of that team's latest container start
    no_wrong_attempts: true             # also flag a solve with zero wrong submissions
    close_solve_window_minutes: 15      # solve within N min after another team's solve

games: []                     # optional allow-list of game IDs for notifications; empty = all games
```

| Key | Meaning |
|---|---|
| `enabled` | Master switch. If false (or file missing), the whole integration is silently disabled. |
| `bot_token` | Bot token. Required; if absent the integration is disabled. Never logged. |
| `first_blood.enabled` / `.channel_id` / `.include_second_third_blood` | Blood announcements; public channel id (17–20 digit snowflake); whether to also post 2nd/3rd blood. Invalid id → first-blood disabled, rest still works. |
| `cheat_detection.enabled` / `.channel_id` / `.show_submitted_flag` | Cheat alerts; private channel id; whether to include the shared flag. Invalid id → cheat alerts disabled, rest still works. |
| `activity_log.enabled` / `.channel_id` | Activity log; private channel the threads live in. Invalid id → activity log disabled, rest still works. **Warning** logged if it equals `first_blood.channel_id`. |
| `activity_log.challenge_types` | `ChallengeType` names to track. Non-container values ignored; empty/none → defaults to `DynamicContainer`. |
| `activity_log.timezone` | Timezone id for report times. Unknown → UTC with a warning. |
| `activity_log.live_feed.enabled` / `.batch_seconds` | Running feed per thread; flush interval (clamped 5–3600). |
| `activity_log.summary.interval_minutes` / `.post_at_game_end` | Periodic report cadence (0 disables); also report at game end. |
| `activity_log.suspicion.*` | The three rule thresholds; `fast_solve_minutes`/`close_solve_window_minutes` = 0 disables that rule. |
| `games` | Allow-list of game IDs for notifications. Empty = all games. |

Channel ids for first blood and cheat may be identical (deduped for the startup check). The activity channel
should be a separate private channel.

**Startup log lines:** enabled → `[Discord] First blood notifications enabled (channel …)`,
`[Discord] Cheat notifications enabled (channel …)`,
`[Discord] Activity log enabled (channel …, types [DynamicContainer], timezone Asia/Dhaka)`,
`[Discord] Channel … is accessible`. Problems disable only the broken part, e.g.
`[Discord] activity_log.channel_id is not a valid Discord channel id; activity log disabled`.

---

## Discord bot setup & permissions

1. **Create the bot:** Developer Portal → New Application → Bot tab → Reset Token → copy into `bot_token`.
   No privileged gateway intents are required (REST only).
2. **Invite with permissions** (OAuth2 → URL Generator, scope `bot`). The bot needs, in each target channel:
   - Always: **View Channel**, **Send Messages**, **Embed Links**.
   - For the **activity log** channel, additionally: **Create Public Threads**, **Send Messages in Threads**,
     **Manage Threads** (unarchive a reused thread), **Read Message History** (find threads to reuse),
     **Attach Files** (upload the timeline `.txt`).
3. **Copy channel ids:** enable Developer Mode, right-click each channel → Copy Channel ID.
4. **Make private channels private:** create a `@Staff` role for admins; on the cheat and activity channels,
   deny `@everyone` **View Channel**, allow `@Staff` and the **bot**. A private category with the channels
   inheriting its permissions is the easy pattern. The public first-blood channel just needs the bot able to
   post.

**Smoke test:** first solve in a game → first-blood embed in the public channel; submit another team's
dynamic flag → cheat embed in the private channel (and the submitter sees `Accepted`); container activity
appears in the per-challenge thread of the activity channel.

---

## Admin API endpoints

Added by the activity log. Both require the **Monitor** role (`[RequireMonitor]`, same as the cheat/monitor
APIs). When the activity log is disabled they return **400** (services not registered).

**Trigger a report** (posts to Discord):
```
POST /api/Game/{gameId}/ActivityLog/Report                  # all challenges with activity
POST /api/Game/{gameId}/ActivityLog/Report?challengeId=12   # one challenge
```
```bash
curl -X POST "https://your-gzctf/api/Game/3/ActivityLog/Report?challengeId=12" -b cookies.txt
# 200 {"title":"Activity report generation queued","status":200}
```

**Export the timeline** (no Discord needed):
```
GET /api/Game/{gameId}/ActivityLog?challengeId=12&format=json   # default json
GET /api/Game/{gameId}/ActivityLog?challengeId=12&format=csv
```
```bash
curl "https://your-gzctf/api/Game/3/ActivityLog?challengeId=12&format=csv" -b cookies.txt -o timeline.csv
```
JSON returns `ActivityLogEntryModel[]`; submitted flags are included only when
`cheat_detection.show_submitted_flag` is true. The existing `GET /api/Game/{id}/CheatInfo` (Monitor) still
lists every `CheatInfo`, now including accepted cheats.

---

## Building & deploying a custom image

`src/GZCTF/Dockerfile` is a thin packaging Dockerfile: it expects the app published into `publish/<platform>`
first. `dotnet publish` also builds the React frontend (the `PublishFrontend` MSBuild target).

### Option A — publish locally, then package (matches upstream CI)
Prereqs: .NET 10 SDK (`global.json` pins `10.0.401`), Node + pnpm, `docker buildx`.
```bash
dotnet publish src/GZCTF/GZCTF.csproj -c Release -o src/GZCTF/publish/linux/amd64

docker buildx build \
  --platform linux/amd64 \
  -f src/GZCTF/Dockerfile \
  -t your-registry/gzctf:extended \
  --load \
  src/GZCTF
```
(For arm64: publish into `publish/linux/arm64` and use `--platform linux/arm64`. With classic `docker build`,
add `--build-arg TARGETPLATFORM=linux/amd64`.)

### Option B — build entirely inside Docker (no local SDK/Node)
Save as `Dockerfile.fullbuild` at the repo root, build with the repo root as context:
```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update && apt-get install -y --no-install-recommends nodejs npm \
    && npm install -g pnpm && rm -rf /var/lib/apt/lists/*
COPY . .
RUN dotnet publish src/GZCTF/GZCTF.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false LC_ALL=en_US.UTF-8
WORKDIR /app
RUN apk add --update --no-cache wget libpcap icu-data-full icu-libs \
    ca-certificates libgdiplus tzdata krb5-libs && update-ca-certificates
COPY --from=build /publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "GZCTF.dll"]
```
```bash
docker build -f Dockerfile.fullbuild -t your-registry/gzctf:extended .
```

### Compose
```yaml
services:
  gzctf:
    image: your-registry/gzctf:extended
    restart: always
    ports: ["8080:8080"]
    volumes:
      - "./appsettings.json:/app/appsettings.json:ro"
      - "./discord.yml:/app/discord.yml:ro"    # <-- mount the config (git-ignored)
      - "./files:/app/files"
    # ...your existing db / redis / env config unchanged
```
To use a different config path set `GZCTF_DISCORD_CONFIG` (e.g. `/config/discord.yml`). Everything else about
deployment (database, redis, reverse proxy, `appsettings.json`) is identical to upstream — see
[gzctf.gzti.me](https://gzctf.gzti.me/).

---

## Database migration

One new table, `ActivityLogEntries`, via EF Core migration **`AddActivityLog`**
(`src/GZCTF/Migrations/20261002130615_AddActivityLog.cs`), with indexes on
`(GameId, ChallengeId, ParticipationId, TimeUtc)` and `(GameId, ChallengeId)`. Migrations apply
**automatically at startup** (`PrelaunchHelper.MigrateAsync`) — no manual step for deployments.

To work on migrations locally:
```bash
dotnet tool install --global dotnet-ef           # once
dotnet ef migrations add <Name> --project src/GZCTF/GZCTF.csproj
```
Features 1 and 2 added **no** migrations.

---

## Building & running the tests

```bash
export DOTNET_ROOT="$HOME/.dotnet" && export PATH="$HOME/.dotnet:$PATH"

dotnet build src/GZCTF/GZCTF.csproj -c Debug                           # backend (0 errors)
dotnet test  src/GZCTF.Test/GZCTF.Test.csproj                          # unit
dotnet test  src/GZCTF.Integration.Test/GZCTF.Integration.Test.csproj  # integration (needs Docker)
```
Integration tests spin up a **Postgres Testcontainer** (Docker required) and apply the migration there.

**Current status:** unit **231 passed**, integration **181 passed**, 0 failures; backend + both test
projects build with 0 errors. Coverage by feature:
- *Accept shared flags* — `AcceptSharedFlagsTests` (5): shared flag accepted + recorded; cheat doesn't take
  blood; cheated solve scores Normal; no duplicate FirstSolve; static unchanged.
- *Discord* — unit tests for config loader, embed factory, API client (429/5xx/network), notifier filters;
  `DiscordNotificationHookTests` (3) end-to-end.
- *Activity log* — 39 unit tests (timeline builder incl. each suspicion rule firing/not, uptime with a
  running container, 2000-char splitting, thread reuse of active/archived threads, multipart upload, logger
  never throws) + 5 integration tests (flag hook fires, faulty logger doesn't break flag checking, entries
  persist/query in order, endpoints enforce the Monitor role).

---

## Working on the code (map of changes)

Everything new lives under `src/GZCTF/Discord/` and `src/GZCTF/Discord/ActivityLog/`; changes to existing
files are hook points and DI wiring only.

### New code
- `src/GZCTF/Discord/` (namespace `GZCTF.Discord`): `DiscordOptions`, `DiscordConfig`, `DiscordConfigLoader`,
  `DiscordNotification`, `IDiscordNotifier` / `NullDiscordNotifier` / `DiscordNotifier`, `DiscordMessage`,
  `DiscordEmbedFactory`, `DiscordApiClient`, `DiscordNotificationService`, `DiscordServiceExtensions`.
- `src/GZCTF/Discord/ActivityLog/` (namespace `GZCTF.Discord.ActivityLog`): `IActivityLogger` (+
  `ContainerDestroyReason`), `NullActivityLogger`, `ActivityLogger`, `RawActivityEvent`, `ActivityLogService`
  (writer + live feed), `ActivitySummaryService` (interval / game-end / on-demand reports),
  `ActivityReportService` (builds + posts reports, CSV), `ActivityTimelineBuilder` (pure, testable core),
  `ActivityReportModels`, `ActivityThreadManager`, `IActivityLogRepository` / `ActivityLogRepository`,
  `ActivityLogEntryModel`.
- `src/GZCTF/Models/Data/ActivityLogEntry.cs` (entity + `ActivityLogType` enum).
- `src/GZCTF/Controllers/ActivityLogController.cs` (admin endpoints).
- Tests under `src/GZCTF.Test/UnitTests/Discord/**` and `src/GZCTF.Integration.Test/Tests/Api/*`.

### Changed existing files (hooks + wiring)
- **Flag acceptance:** `Repositories/GameInstanceRepository.cs` (cheat folded into `VerifyAnswer`, blood
  exclusion, returns `ChallengeType`), `Repositories/Interface/IGameInstanceRepository.cs` (removed
  `CheckCheat`), `Services/FlagChecker.cs` (`Accepted`+`CheatDetected` together; activity + Discord hooks),
  `Controllers/GameController.cs` (`Status` maps cheat→Accepted; extend hook), `Repositories/GameRepository.cs`
  (`GenScoreboard` `IsCheat`), `Utils/Shared.cs` (`VerifyResult` gained `Cheat` and `ChallengeType`).
- **Discord/activity wiring:** `Extensions/Startup/ServicesExtension.cs` (`AddDiscordIntegration()`),
  `Models/AppDbContext.cs` (`DbSet<ActivityLogEntry>`), `Directory.Packages.props` + `GZCTF.csproj`
  (YamlDotNet), `.gitignore` (ignore `discord.yml`), `discord.example.yml`.
- **Container activity hooks:** `Repositories/ContainerRepository.cs` +
  `Repositories/Interface/IContainerRepository.cs` (`DestroyContainer` gained `ContainerDestroyReason`,
  captures the instance key, logs after commit), and its callers (`AdminController`, `EditController`,
  `ExerciseInstanceRepository`, `Services/CronJob/RuntimeCronJobs.cs`) pass the right reason;
  `GameInstanceRepository.CreateContainer` logs start/fail; `GameController.ExtendContainerLifetime` logs
  extend; `FlagChecker` logs the flag result.

### Guarantees to preserve when editing
- Logging/notification hooks must stay **fire-and-forget** (enqueue only, never throw, off the hot path).
- Don't add values to the existing `EventType`/`AnswerResult`/`SubmissionType` enums the frontend uses; the
  activity log has its own `ActivityLogType` on its own table.
- Keep the `pg_advisory_xact_lock` + `alreadySolved` guard in `VerifyAnswer` so a cheated solve can't
  double-insert a `FirstSolve`/`CheatInfo`.
- The scoreboard reads `FirstSolves`, not `Submission.Status` — preserve that when touching scoring.

---

## Limitations & follow-ups

- **Static challenges** can be *tracked* by the activity log (`StaticContainer`), but flag **sharing** on
  static challenges is undetectable (all teams share one flag) — so Feature 1's acceptance and Feature 2's
  cheat alerts apply to **dynamic** challenges only.
- **Auto-destroy-after-solve is best-effort:** the frontend's post-solve destroy reuses the manual-delete
  endpoint, so it's inferred as `ContainerAutoDestroyedAfterSolve` only when a `FirstSolve` already exists at
  destroy time, otherwise recorded as `ContainerDestroyedByUser`.
- **At-least-queued, not guaranteed-delivered:** a crash in the brief window between enqueue and the
  background write can lose that one in-flight notification/entry; the stored activity timeline itself
  survives restarts (it's a DB table). This keeps the hot path non-blocking.
- **Config is read once at startup;** editing `discord.yml` needs a restart.
- **Container lifecycle events** need a real container manager, so they're exercised in production/manual
  runs; the integration tests cover the flag-result path end-to-end and the rest via unit tests.
- **Dynamic score inflation:** an accepted cheated solve counts toward `solvedCount` like any solve, lowering
  everyone's dynamic score — intended, but worth watching for games with heavy sharing.
- **No message templating/localization** for Discord text yet (English, fixed). **REST only** (the bot never
  shows "online"). The frontend `Api.ts` was not regenerated (no frontend source changed).

---

## Credits & license

- **Base platform:** [GZ::CTF](https://github.com/GZTimeWalker/GZCTF) by
  [@GZTimeWalker](https://github.com/GZTimeWalker) and its contributors. Documentation:
  [gzctf.gzti.me](https://gzctf.gzti.me/). All credit for the CTF platform itself goes to the upstream
  project — please support it.
- **This fork** adds only the three features documented above and is maintained independently; it is not
  affiliated with or endorsed by the upstream maintainers.
- **License:** this project inherits the upstream license — see [`LICENSE.txt`](./LICENSE.txt) and
  [`LICENSE_ADDENDUM.txt`](./LICENSE_ADDENDUM.txt). All upstream license and attribution terms continue to
  apply to the fork.
```
