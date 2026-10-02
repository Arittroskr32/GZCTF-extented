# Container Activity Log — Implementation

Branch: `feature/activity-log` (based on `feature/discord-notifications`).

An append-only, per-challenge, per-team timeline of **container lifecycle events** and **flag
submissions** for container challenges, delivered to a **private** Discord channel (one thread per
challenge) plus admin API endpoints (JSON/CSV). It is built to help spot suspiciously fast or shared
solves (e.g. a team that receives a solve script, starts its own container, and submits its own correct
flag within minutes). It reuses the existing Discord integration's queue, rate-limited HTTP sender,
markdown escaping and `allowed_mentions` protection. Disabling it leaves the rest of GZCTF (and the rest of
the Discord integration) working unchanged.

See `activity-log-analysis.md` for the Phase-1 investigation (every hook point and which existing
`GameEvent`s miss which lifecycle events).

---

## 1. Files added / changed

### Added — `src/GZCTF/Discord/ActivityLog/` (namespace `GZCTF.Discord.ActivityLog`)
| File | Purpose |
|---|---|
| `IActivityLogger.cs` | Fire-and-forget hook API + `ContainerDestroyReason` enum. Every method is non-blocking and never throws into the caller. |
| `NullActivityLogger.cs` | No-op used when the feature is disabled, so hook points are unconditional. |
| `ActivityLogger.cs` | Real logger: filters by tracked challenge type (where known) and enqueues a `RawActivityEvent` onto a bounded channel; drops with a warning if full. |
| `RawActivityEvent.cs` | Lightweight queue record (fully-formed events, or a destroy marker resolved later). |
| `ActivityLogService.cs` | `BackgroundService` draining the queue: resolves + filters + persists each entry, then (if live feed enabled) batches entries per challenge and posts to the thread every `batch_seconds`. |
| `ActivitySummaryService.cs` | `BackgroundService` driving reports: drains on-demand report requests and, on a 1-minute heartbeat, enqueues periodic reports for running games and a final report at game end. |
| `ActivityReportService.cs` | Builds a challenge report from stored entries and posts the summary embed + full timeline `.txt` to the thread; also builds the CSV export. |
| `ActivityTimelineBuilder.cs` | **Pure** report/timeline builder: per-team uptime, counts, time-to-solve, the three suspicion rules, plain-text timeline, live-feed line formatting, 2000-char message splitting, destroy-reason mapping. Fully unit-testable. |
| `ActivityReportModels.cs` | `ReportContext`, `TeamTimeline`, `ChallengeReport` records (builder I/O). |
| `ActivityThreadManager.cs` | Resolves/caches the per-challenge thread: finds an existing thread by `#<id>` name among active **and** archived threads (unarchiving a match) before creating one, so restarts don't duplicate. |
| `IActivityLogRepository.cs` / `ActivityLogRepository.cs` | Append + timeline queries over the new table; `AddAsync` never throws (returns false on failure). |
| `ActivityLogEntryModel.cs` | Admin JSON DTO (gates the flag field on `show_submitted_flag`). |

### Added — entity, migration, controller, tests
| File | Purpose |
|---|---|
| `src/GZCTF/Models/Data/ActivityLogEntry.cs` | Append-only entity + self-contained `ActivityLogType` enum (separate from `EventType`). Indexes on `(GameId, ChallengeId, ParticipationId, TimeUtc)` and `(GameId, ChallengeId)`. |
| `src/GZCTF/Migrations/20261002130615_AddActivityLog.cs` | EF Core migration creating `ActivityLogEntries` + its indexes. **Migration name: `AddActivityLog`.** |
| `src/GZCTF/Controllers/ActivityLogController.cs` | Admin/monitor endpoints (`[RequireMonitor]`): trigger a report, and export the timeline as JSON/CSV. |
| `src/GZCTF.Test/UnitTests/Discord/ActivityLog/*` | 39 unit tests (builder, config, thread manager, file upload, logger). |
| `src/GZCTF.Integration.Test/Tests/Api/ActivityLogTests.cs` | 5 integration tests (hook fires, fault isolation, persistence/migration, endpoint auth). |

### Changed (hook points + config/DI wiring only)
| File | Change |
|---|---|
| `src/GZCTF/Discord/DiscordOptions.cs` | Added `activity_log` YAML shape (`ActivityLogOptions` + live_feed/summary/suspicion sub-objects). |
| `src/GZCTF/Discord/DiscordConfig.cs` | Added validated `ActivityLogConfig`; `AnyEnabled` and `ActiveChannelIds` now include the activity channel. |
| `src/GZCTF/Discord/DiscordConfigLoader.cs` | Validates `activity_log` (channel snowflake, container-type filter + default, timezone fallback, same-as-first-blood warning); a broken section disables only itself. |
| `src/GZCTF/Discord/DiscordApiClient.cs` | Added thread listing (active + archived), create, unarchive, and multipart file upload; refactored the retry/rate-limit loop into a shared helper reused by the file upload. |
| `src/GZCTF/Discord/DiscordMessage.cs` | Added optional `content` field (for live-feed text messages). |
| `src/GZCTF/Discord/DiscordServiceExtensions.cs` | Registers the activity-log channels, logger, thread manager, repository, report service and two hosted services; registers `NullActivityLogger` when disabled. |
| `src/GZCTF/Models/AppDbContext.cs` | Added `DbSet<ActivityLogEntry>`. |
| `src/GZCTF/Repositories/Interface/IContainerRepository.cs` · `ContainerRepository.cs` | `DestroyContainer` gained a `ContainerDestroyReason` parameter; it captures the owning instance key before removal and logs the destruction afterwards (captures **all** destroys incl. expiry/admin). |
| `src/GZCTF/Repositories/GameInstanceRepository.cs` | `CreateContainer` logs `ContainerStarted` / `ContainerStartFailed`; `VerifyAnswer` now returns the `ChallengeType` on `VerifyResult`. |
| `src/GZCTF/Controllers/GameController.cs` | `ExtendContainerLifetime` logs `ContainerExtended`. |
| `src/GZCTF/Services/FlagChecker.cs` | Logs the final flag result (`FlagWrong`/`FlagAccepted`+tier/`FlagCheatDetected`+source) after the submission is fully processed. |
| `src/GZCTF/Utils/Shared.cs` | `VerifyResult` gained an optional `ChallengeType?` (so the flag hook can filter by type without an extra query). |
| callers of `DestroyContainer` (`AdminController`, `EditController`, `ExerciseInstanceRepository`, `RuntimeCronJobs`) | Pass the correct `ContainerDestroyReason` (Admin / ChallengeRemoval / LimitReached / Expired). |
| `discord.example.yml` | Documented `activity_log` section. |

No existing `EventType` values or enums the frontend uses were changed; no frontend files were touched.

### Hook points (and which existing GameEvents they fill in)
- **Container started / start-failed:** `GameInstanceRepository.CreateContainer` (start already had a `ContainerStart` GameEvent; **start-failure was previously unlogged**).
- **Container extended:** `GameController.ExtendContainerLifetime` (**previously unlogged**).
- **Container destroyed (all paths):** inside `ContainerRepository.DestroyContainer`, the single choke point. The reason distinguishes user / auto-after-solve / limit / **expiry (cron, previously unlogged)** / **admin (previously unlogged)** / challenge-removal. A user destroy of an already-solved challenge is recorded as `ContainerAutoDestroyedAfterSolve` (best-effort — see Limitations).
- **Flag result:** `FlagChecker.Checker`, recorded last so it can never affect scoring/events.

---

## 2. `activity_log` config reference

Add this section to `discord.yml` (full example in `discord.example.yml`):

```yaml
activity_log:
  enabled: true
  channel_id: "345678901234567890"      # PRIVATE channel; threads are created inside it
  challenge_types: ["DynamicContainer"] # add "StaticContainer" to also track those
  timezone: "Asia/Dhaka"                # IANA/Windows id; used for report times (default UTC)
  live_feed:
    enabled: true
    batch_seconds: 30                   # one message per challenge thread per interval
  summary:
    interval_minutes: 60                # periodic full report; 0 = disabled
    post_at_game_end: true
  suspicion:
    fast_solve_minutes: 10              # solve within N min of that team's latest container start
    no_wrong_attempts: true            # also flag a solve with zero wrong submissions
    close_solve_window_minutes: 15      # solve within N min after another team's solve
```

| Key | Meaning |
|---|---|
| `enabled` | Master switch for the activity log. If false (or section absent), only this feature is disabled. |
| `channel_id` | PRIVATE channel (admins + bot). Invalid → activity log disabled, rest of Discord still works. A startup **warning** is logged if it equals `first_blood.channel_id`. |
| `challenge_types` | `ChallengeType` names to track. Non-container values are ignored; empty/none → defaults to `DynamicContainer`. |
| `timezone` | Timezone id for report times. Unknown id → falls back to UTC with a warning. |
| `live_feed.enabled` | Post a running compact feed into each challenge's thread. |
| `live_feed.batch_seconds` | Flush interval for batched live-feed lines (clamped 5–3600). |
| `summary.interval_minutes` | Minutes between periodic reports; 0 disables them. |
| `summary.post_at_game_end` | Also post a final report when a game ends. |
| `suspicion.fast_solve_minutes` | Flag a solve within N minutes of that team's most recent container start (0 disables the rule). |
| `suspicion.no_wrong_attempts` | Also flag a solve that had zero wrong submissions. |
| `suspicion.close_solve_window_minutes` | Flag a solve within N minutes after another team's solve of the same challenge (0 disables the rule). |

Config is read once at startup; editing it requires a restart (same as the rest of `discord.yml`).

---

## 3. Private channel + bot permissions

1. Create a **private** channel (or category), e.g. `#activity-log`: deny `@everyone` **View Channel**, allow your `@Staff` role and the **bot**. This is where the per-challenge threads live. Do **not** reuse the public first-blood channel.
2. The bot now needs, in that channel, in addition to View Channel / Send Messages / Embed Links:
   - **Create Public Threads** — to open a thread per challenge.
   - **Send Messages in Threads** — to post the live feed and reports.
   - **Manage Threads** — to unarchive a reused (archived) thread after a restart.
   - **Read Message History** — to list existing threads for reuse.
   - **Attach Files** — to upload the full timeline `.txt`.
   Rebuild the OAuth2 invite URL (Developer Portal → OAuth2 → URL Generator, scope `bot`) with those permissions, or grant them on the channel/role.
3. Copy the channel id (Developer Mode → right-click channel → Copy Channel ID) into `activity_log.channel_id`.

Startup log lines to look for:
- `[Discord] Activity log enabled (channel …, types [DynamicContainer], timezone Asia/Dhaka)`
- `[Discord] Channel … is accessible`
- Problems: `[Discord] activity_log.channel_id is not a valid Discord channel id; activity log disabled`, or the same-as-first-blood warning.

---

## 4. Admin endpoints

All require the **Monitor** role (`[RequireMonitor]`, the same attribute the cheat/monitor APIs use). When
the activity log is disabled, they return **400** (the backing services are not registered).

### Trigger a report (posts to Discord)
```
POST /api/Game/{gameId}/ActivityLog/Report            # all challenges with activity
POST /api/Game/{gameId}/ActivityLog/Report?challengeId=12   # one challenge
```
Example:
```bash
curl -X POST "https://your-gzctf/api/Game/3/ActivityLog/Report?challengeId=12" \
     -b cookies.txt     # authenticated as a Monitor/Admin
# 200 {"title":"Activity report generation queued","status":200}
```

### Export the timeline (no Discord needed)
```
GET /api/Game/{gameId}/ActivityLog?challengeId=12&format=json   # default json
GET /api/Game/{gameId}/ActivityLog?challengeId=12&format=csv
```
Example:
```bash
curl "https://your-gzctf/api/Game/3/ActivityLog?challengeId=12&format=csv" -b cookies.txt -o timeline.csv
```
JSON returns `ActivityLogEntryModel[]`; submitted flags are included only when
`cheat_detection.show_submitted_flag` is true.

---

## 5. Example output

**Summary embed** (posted in the challenge's thread): challenge title, teams started, solves, flagged-team
count, and the list of suspicious teams with reasons.

**Attached `challenge-12-timeline.txt`** (sorted by team id, then time, in the configured timezone):
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

Team #7 "BetaPwn"
  ...
  Summary: total container uptime 58m, 3 starts, 5 wrong flags, solved after 58m
```

**Live feed** (compact, one line per event, batched; never includes flag values):
```
`11:17:02` Team #3 "AlphaSec" — bob — ✗ wrong flag <t:1760000000:T>
`11:18:30` Team #3 "AlphaSec" — bob — ✓ solved — Normal <t:1760000000:T>
```

Per team the report computes: total container uptime (a container still running at report time counts up
to the report time), number of container starts, number of wrong submissions, time from first container
start to solve, time from latest container start to solve, and the matched suspicion reasons.

---

## 6. Security

- Activity data is posted **only** to the configured private `activity_log.channel_id` (threads inside it). Nothing is ever posted to the public first-blood channel; a startup warning fires if the two channel ids match.
- The **live feed never includes flag values**. Submitted (wrong) flags appear only in the attached report `.txt` / CSV / JSON, and only when `cheat_detection.show_submitted_flag` is true.
- All user-controlled text (team names, usernames, challenge titles) is markdown-escaped, and every message sets `allowed_mentions: { parse: [] }` (reusing the existing sender), so nothing can ping anyone.

---

## 7. Reliability / "never breaks the original action"

- Hooks only **enqueue** (a lock-free `TryWrite`) onto a bounded channel (capacity 5000) and are wrapped so they never throw; a full queue drops with a warning. Container start/stop and flag checking are never blocked.
- Resolution (challenge type, team names) and persistence happen in the background `ActivityLogService`; `ActivityLogRepository.AddAsync` catches and logs any DB failure (returns false).
- The destroy hook resolves the instance key **before** the container row is removed, and does the rest after the destroy has committed — a logging failure can never affect the destroy.
- All Discord sends go through the existing rate-limited client (429 `retry_after`, 5xx/network backoff). If Discord is down, entries are still stored and a report can be regenerated later via the admin endpoint.

---

## 8. Test results

- **Unit tests** (`GZCTF.Test`): **231 passed, 0 failed** — includes **39 new** activity-log tests:
  - `ActivityTimelineBuilderTests`: team ordering + counts; uptime pairing incl. a container still running at report time; each suspicion rule firing **and** not firing (fast-solve, no-wrong, close-solve); timeline text shape; destroy-reason→type mapping; duration formatting; 2000-char message splitting incl. hard-split; live line never contains a flag.
  - `ActivityConfigLoaderTests`: full parse; invalid channel disables only activity; non-container types default to DynamicContainer; invalid timezone → UTC; activity-only still loads; activity-off leaves other features working.
  - `ActivityThreadManagerTests`: `#id` name-boundary matching; reuse active thread (no create); reuse archived thread (unarchive, no create); create when none + cache (one create).
  - `ActivityFileUploadTests`: multipart with `payload_json` + file + empty `allowed_mentions`; 5xx retry.
  - `ActivityLoggerTests`: tracked enqueued / untracked dropped; result→type mapping; destroy always enqueued; queue-full never throws.
- **Integration tests** (`GZCTF.Integration.Test`): includes **5 new** `ActivityLogTests`:
  - flag-result hook fires (accepted + wrong) on a tracked dynamic challenge through the real pipeline;
  - shared-flag solve records a cheat result;
  - a **faulty (throwing) activity logger does not break flag checking** (solve still recorded);
  - entries persist and query back in team/time order (confirms the migration applied in the test DB);
  - admin endpoints enforce the Monitor role (401 anonymous, 403 regular user, 400 when disabled).

  Full integration suite: **181 passed, 0 failed** (176 pre-existing + 5 new; nothing regressed).

Backend, unit-test and integration-test projects all build with **0 errors**. The EF migration
`AddActivityLog` applies automatically at startup (`PrelaunchHelper.MigrateAsync`) and was applied in the
integration test environment (Postgres Testcontainer).

---

## 9. Limitations / anything not captured

- **Container lifecycle events require a real container manager**, so they are exercised in production/manual runs, not in the integration tests (which simulate dynamic flags without starting containers). The destroy choke point, start/fail and extend hooks are covered by construction + unit tests; the flag-result path is covered end-to-end.
- **Auto-destroy-after-solve is best-effort.** The frontend's post-solve destroy uses the *same* DELETE endpoint as a manual delete, so it is indistinguishable server-side; the log infers `ContainerAutoDestroyedAfterSolve` when a `FirstSolve` already exists for that team+challenge at destroy time, otherwise records `ContainerDestroyedByUser`.
- **Static challenges** can be tracked (`StaticContainer`) since each team still gets its own container, but flag **sharing** on static challenges remains undetectable (all teams share one flag) — the same limitation as the existing cheat detection. Static-*attachment*/*attachment* types are never tracked (no container).
- **At-least-queued, not guaranteed-delivered.** A crash in the brief window between enqueue and the background DB write can lose that one in-flight event; the stored timeline itself survives restarts (it is a DB table). This mirrors the existing Discord integration's design and keeps the hot path non-blocking.
- **Config is read once at startup;** editing `discord.yml` needs a restart.
- **Challenge-removal bulk destroys** (`DestroyAllContainers`) are recorded per container as `ContainerDestroyedOnChallengeRemoval`; the challenge may already be mid-deletion, so a report for a since-deleted challenge resolves no title and is skipped.
- **Thread discovery lists public archived threads** for reuse; a thread manually converted to private, or archived beyond Discord's returned page, may not be found and a new one would be created.
