# Activity Log — Phase 1 Investigation

Branch: `feature/activity-log` (based on `feature/discord-notifications`).

> Goal of this document: locate **every** place a container lifecycle event or a flag submission
> happens, note which ones already emit a `GameEvent` and which do **not**, so the activity log can be
> wired to capture a *complete* timeline (including events the existing `GameEvent` log misses, such as
> automatic expiry). All line numbers reflect the tree at investigation time.

---

## 1. Container lifecycle

### 1.1 Container **created / started**

**`GameInstanceRepository.CreateContainer`** — `src/GZCTF/Repositories/GameInstanceRepository.cs:116-216`.
This is the single creation path for a **team game instance** container.

- Success path sets `gameInstance.Container`, `LastContainerOperation`, and **emits `EventType.ContainerStart`** (`GameInstanceRepository.cs:199-207`) with `TeamId`, `UserId`, `Values=[challengeId, title]`.
- Callers:
  - **Player:** `GameController.CreateContainer` → `POST /api/Game/{id}/Container/{challengeId}` — `GameController.cs:1209-1256`, calls `gameInstanceRepository.CreateContainer` at `:1246`.
  - No separate admin path creates a *team-instance* container. (`EditController.CreateTestContainer`, `EditController.cs:758-803`, creates a challenge **test** container via `containerService.CreateContainerAsync` directly — it has **no `GameInstance`/team**, so it is out of scope for a team activity log.)

➡️ **Already logged** as a `GameEvent` (`ContainerStart`). Activity log will record `ContainerStarted` at the same success point.

### 1.2 Container **start failure**

**`GameInstanceRepository.CreateContainer`** returns `TaskResult<Container>(TaskStatus.Failed)` at:
- `:126` — missing image / expose port (config error).
- `:190` — `service.CreateContainerAsync` returned null (orchestrator failure).

➡️ **NOT logged** as a `GameEvent` (only a `SystemLog` line). **Gap** → activity log will record `ContainerStartFailed`.

### 1.3 Container lifetime **extended**

**`ContainerRepository.ExtendLifetime`** — `src/GZCTF/Repositories/ContainerRepository.cs:41-45` (`container.ExpectStopAt += time`).
- Caller: `GameController.ExtendContainerLifetime` → `POST /api/Game/{id}/Container/{challengeId}/Extend` — `GameController.cs:1277-1307`, calls `containerRepository.ExtendLifetime` at `:1303`.

➡️ **NOT logged** as a `GameEvent`. **Gap** → activity log will record `ContainerExtended`.

### 1.4 Container **destroyed** — single choke point

**ALL destroys funnel through `ContainerRepository.DestroyContainer`** — `src/GZCTF/Repositories/ContainerRepository.cs:50-77`. It archives traffic, destroys via the orchestrator, removes the row, and returns a bool. It does **not** know *why* it was called. Callers and their reasons:

| # | Caller | File · line | Reason | GameEvent today? |
|---|---|---|---|---|
| 1 | Player delete (`DeleteContainer`, `DELETE .../Container/{cid}`) | `GameController.cs:1355` | user manual delete | **Yes** — emits `EventType.ContainerDestroy` at `GameController.cs:1360-1368` |
| 2 | Frontend auto-destroy after a correct flag | `GameChallengeModal.tsx:120,151` → `requestDestroy()` → `gameDeleteContainer` | auto after solve | **Yes, but** it calls the *same* DELETE endpoint as #1 → **indistinguishable** server-side (same `ContainerDestroy` event) |
| 3 | Auto-destroy when `ContainerCountLimit` reached | `GameInstanceRepository.cs:146` | limit eviction | **No** (only a `logger.Log`) — **gap** |
| 4 | Automatic **expiry** cron | `RuntimeCronJobs.ContainerChecker` `src/GZCTF/Services/CronJob/RuntimeCronJobs.cs:14-29` (`*/3 * * * *`, iterates `GetDyingContainers` where `ExpectStopAt < now`) | expiry | **No** — **gap (the important one)** |
| 5 | Admin destroy (`DestroyInstance`) | `AdminController.cs:674` (`DELETE /api/Admin/Instances/{id}`, `[RequireAdmin]`) | admin | **No** — **gap** |
| 6 | Challenge disabled / type changed | `EditController.cs:723` → `GameInstanceRepository.DestroyAllContainers` (`GameInstanceRepository.cs:218-231`) | challenge removed | **No** — bulk, out of per-team interest but still a destroy |
| 7 | Test container destroy | `EditController.cs:831` | admin test | No `GameInstance` → out of scope |

**Design consequence:** because `DestroyContainer` is the one place every destroy passes through, the most
*complete* capture is to log there. We add a `ContainerDestroyReason` parameter (default `Unknown`) so each
caller states the reason; the activity logger maps it to the entry type. Expiry (#4) and admin (#5) — the
two biggest gaps — are then captured for free. Entry types:

- #1 → `ContainerDestroyedByUser` (upgraded to `ContainerAutoDestroyedAfterSolve` when a `FirstSolve`
  already exists for that participation+challenge at delete time — the only server-side signal that the
  delete followed a solve; best-effort, see Limitations).
- #3 → `ContainerDestroyedOnLimit` (extra type; system eviction).
- #4 → `ContainerExpired`.
- #5 → `ContainerDestroyedByAdmin`.
- #6 → `ContainerDestroyedOnChallengeRemoval` (bulk).

`GetDyingContainers` (`ContainerRepository.cs:38-39`) and `GetContainerById` (`:20-21`) return a bare
`Container` without the `GameInstance` graph, so the logger re-reads `GameInstance` (by
`container.GameInstanceId`) to resolve game/challenge/participation/team. That read happens **after** the
destroy has already committed, wrapped in try/catch — it can never affect the destroy.

---

## 2. Flag submissions & results

**`FlagChecker.Checker`** — `src/GZCTF/Services/FlagChecker.cs:98-200` (post the accept-shared-flags change).

- `VerifyAnswer` returns `(type, ans, cheat)`.
- `AnswerResult.Accepted` **and** `AnswerResult.CheatDetected` are handled together (`:111-161`): both are accepted/scored; `CheatDetected` additionally emits the `CheatDetected` game event and the Discord cheat alert.
- `AnswerResult.NotFound` → `:107-110`.
- `default` (WrongAnswer) → `:162-174`.
- Blood tier is the `SubmissionType type` (`FirstBlood/SecondBlood/ThirdBlood/Normal/Unaccepted`); a public blood `GameNotice` + Discord first-blood fire only for blood tiers (`:176-189`). Cheated solves are forced to `Normal`, so they never post blood.

Events today: `FlagSubmit` (`EventType.FlagSubmit`) is emitted on both accept and wrong; `CheatDetected`
(`EventType.CheatDetected`) on cheats. These carry team/user/challenge but **not** the blood tier in a
structured way, and the activity log wants per-type rows.

➡️ Activity log will record, from the same switch, with the final `AnswerResult` + `SubmissionType`:
`FlagWrong`, `FlagAccepted` (+ blood tier), `FlagCheatDetected` (+ source team). The `Submission` row
already carries `GameId, ChallengeId, ParticipationId, TeamId, UserId, SubmitTimeUtc, Answer`
(`src/GZCTF/Models/Data/Submission.cs:13-110`) — everything an entry needs, resolved on the hot path with
no extra query.

---

## 3. `EventType` enum (existing — do NOT extend)

`src/GZCTF/Utils/Enums.cs:190-216`: `Normal=0`, `ContainerStart=1`, `ContainerDestroy=2`, `FlagSubmit=3`,
`CheatDetected=4`. The frontend monitor (`Events.tsx`) renders these. **We will not add values here.** The
activity log uses its **own** `ActivityLogType` enum on its **own** table, leaving `GameEvent` untouched.

---

## 4. Persistence & infrastructure facts

- **Auto-migrate at startup:** `PrelaunchHelper.cs:26` calls `context.Database.MigrateAsync()` (also
  `EntityConfigurationProvider.cs:104`). So a new EF migration applies automatically on boot — no manual step.
- **DbContext:** `src/GZCTF/Models/AppDbContext.cs` — add one `DbSet<ActivityLogEntry>` (line ~43 area).
  Entities configure via data annotations (`[Index]`, `[Required]`) like `Container.cs:8-9`.
- **Migrations dir:** `src/GZCTF/Migrations/` (latest `20251203143253_AddNetworkMode`). Create with
  `dotnet ef migrations add AddActivityLog -p src/GZCTF`.
- **Admin auth attributes:** `src/GZCTF/Middlewares/PrivilegeAuthentication.cs` — `[RequireUser]`,
  `[RequireMonitor]` (`:80`, `Role.Monitor`), `[RequireAdmin]` (`:85`). The cheat/monitor APIs use
  `[RequireMonitor]`; the new report/export endpoints will use **`[RequireMonitor]`** to match.
- **Existing Discord plumbing to reuse (do not duplicate):** bounded `Channel<T>` queue + hosted drainer
  (`DiscordServiceExtensions.cs`, `DiscordNotificationService.cs`), typed `HttpClient` with 429/5xx/backoff
  (`DiscordApiClient.cs`), markdown escaping + `allowed_mentions:[]` (`DiscordEmbedFactory.cs`,
  `DiscordMessage.cs`), config load/validate pattern (`DiscordConfigLoader.cs`), and the
  `Null*` fallback when disabled (`NullDiscordNotifier.cs`).

---

## 5. Gaps summary (what the current `GameEvent` log misses)

| Lifecycle point | Emits GameEvent today | Captured by activity log |
|---|---|---|
| Container started | ✅ `ContainerStart` | ✅ `ContainerStarted` |
| Container start failed | ❌ | ✅ `ContainerStartFailed` |
| Container extended | ❌ | ✅ `ContainerExtended` |
| Destroyed by user | ✅ `ContainerDestroy` | ✅ `ContainerDestroyedByUser` |
| Auto-destroy after solve | ⚠️ as user destroy (same endpoint) | ✅ best-effort `ContainerAutoDestroyedAfterSolve` |
| Destroyed on count limit | ❌ | ✅ `ContainerDestroyedOnLimit` |
| **Expired (cron)** | ❌ | ✅ `ContainerExpired` |
| Destroyed by admin | ❌ | ✅ `ContainerDestroyedByAdmin` |
| Destroyed on challenge removal | ❌ | ✅ `ContainerDestroyedOnChallengeRemoval` |
| Flag wrong / accepted / cheat / blood | ✅ `FlagSubmit`/`CheatDetected` | ✅ `FlagWrong`/`FlagAccepted(+tier)`/`FlagCheatDetected(+source)` |

**No blocker found.** The single-choke-point `DestroyContainer` plus the `FlagChecker` switch give a
complete capture surface; persistence, migrations, auth, and the Discord queue/sender all already exist to
build on. Proceeding to implementation.
