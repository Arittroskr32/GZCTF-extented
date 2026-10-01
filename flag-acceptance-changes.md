# Accept Shared Flags — Implementation Changes

Branch: `feature/accept-shared-flags`

**Goal implemented:** when a team submits another team's **dynamic** flag (DynamicAttachment / DynamicContainer) for the same challenge, the submission is now **accepted** (the submitting team gets the solve and dynamic points) while the cheat is **still detected and recorded** exactly as before (a `CheatInfo` row, a `CheatDetected` game event, the cheat log line, and the admin-monitor SignalR push). Static challenges are unchanged. A cheated solve never earns or occupies a blood bonus.

---

## 1. Files changed

### Backend

| File | Change |
|---|---|
| `src/GZCTF/Utils/Shared.cs` | Extended the `VerifyResult` record with an optional `CheatCheckInfo? Cheat` field so `VerifyAnswer` can tell the caller a solve was an accepted shared-flag cheat (and pass the source-team name for the event). Added `using GZCTF.Models.Internal;`. |
| `src/GZCTF/Repositories/GameInstanceRepository.cs` | Core change. Removed the old post-hoc `CheckCheat` method and moved cheat detection **into `VerifyAnswer`**, inside the same transaction and after the same `pg_advisory_xact_lock`. New private helpers `FindCheatSource` (locates another participation's instance whose `FlagContext.Flag` equals the answer — the former `CheckCheat` query) and `RecordCheat` (creates the `CheatInfo` idempotently on the unique `SubmissionId` index and returns a `CheatCheckInfo`). A shared dynamic flag now sets the submission status to `CheatDetected` **and** proceeds through the accept path (FirstSolve insert, `alreadySolved` guard). Blood is forced off for cheated solves (`!isCheat` in `hasBloodPermission`). `CountBloodEligibleSolves` now excludes FirstSolves that have a matching `CheatInfo`, so a cheater never occupies a blood slot. |
| `src/GZCTF/Repositories/Interface/IGameInstanceRepository.cs` | Removed the now-unused `CheckCheat` method from the interface (cheat logic now lives in one place, `VerifyAnswer`). |
| `src/GZCTF/Services/FlagChecker.cs` | `VerifyAnswer` is now destructured as `(type, ans, cheat)`. `AnswerResult.CheatDetected` is handled together with `AnswerResult.Accepted`: it logs the accept, writes the `FlagSubmit` event, flushes the scoreboard cache, and — when `CheatDetected` — additionally writes the `CheatDetected` game event and the cheat log line (same values/message as before) using `cheat.SourceTeamName`. The old `default:` branch no longer calls `CheckCheat`. The final `item.Status` written is still the real status (`CheatDetected` for a cheat). The blood-notice guard (`type != Normal`) already prevents any blood `GameNotice` for a cheated solve because its `SubmissionType` is forced to `Normal`. |
| `src/GZCTF/Controllers/GameController.cs` | `Status` endpoint now maps `AnswerResult.CheatDetected → Accepted` (was `→ WrongAnswer`), so the submitting player sees the exact same result as a legitimate correct flag. |
| `src/GZCTF/Repositories/GameRepository.cs` | `GenScoreboard`: `SolveSnapshot` gained an `IsCheat` flag, populated from a correlated `CheatInfo` existence check in the FirstSolves query. A cheated solve is still score-eligible and still counts toward `solvedCount` (dynamic scoring), but `bloodEligible` is forced false, so cheated solves never take a blood tier and the next legitimate solver still gets the blood here too. |

### Tests

| File | Change |
|---|---|
| `src/GZCTF.Integration.Test/Base/TestDataSeeder.cs` | Added `CreateDynamicChallengeAsync` (enabled DynamicContainer challenge) and `SetInstanceFlagAsync` (assigns a specific per-team flag to a team's game instance, simulating dynamic flag dispatch) so dynamic-flag scenarios can be set up without starting real containers. |
| `src/GZCTF.Integration.Test/Tests/Api/AcceptSharedFlagsTests.cs` | New test class (5 tests) covering the required scenarios. |

No frontend source files were changed (see §3).

---

## 2. `AnswerResult` audit

Every place that reads/branches on `AnswerResult.Accepted` / `AnswerResult.CheatDetected`, and the decision taken:

**Backend**
- `GameInstanceRepository.VerifyAnswer` — rewritten. Own-flag → `Accepted`; shared dynamic flag → `CheatDetected` **and** accepted path; otherwise `WrongAnswer`. ✔ changed intentionally.
- `GameInstanceRepository.CheckCheat` — **removed** (logic folded into `VerifyAnswer`).
- `GameInstanceRepository.CountBloodEligibleSolves` — excludes cheated FirstSolves from blood counting. ✔ changed.
- `FlagChecker.Checker` — `Accepted` and `CheatDetected` handled together (accept + record cheat). ✔ changed.
- `GameController.Status` (`.../Status/{submitId}`) — `CheatDetected → Accepted` for the player. ✔ changed.
- `GameController.Submit` — sets initial status `FlagSubmitted` only; unaffected. ✔ unchanged.
- `GameRepository.GenScoreboard` — **does not read `Submission.Status`.** It computes solved/score/blood entirely from the `FirstSolves` table joined to submissions, so a cheated solve (which now has a `FirstSolve`) is correctly treated as solved and scored. Blood tiering excludes cheated solves. ✔ this is the key "solved = FirstSolve, not status" guarantee; adjusted only for blood.
- `GameRepository` scoreboard uses `ParticipationStatus.Accepted` (a different enum) — unrelated. ✔ unchanged.
- `SubmissionRepository.GetSubmissions(..., AnswerResult? type)` / `GetUncheckedFlags` — admin Submissions monitor + Excel export filter by `Submission.Status`. A cheated solve keeps status `CheatDetected`, so it still shows under the `CheatDetected` filter for admins (as before). `GetUncheckedFlags` still only requeues `FlagSubmitted`, so nothing is stranded. ✔ unchanged, verified correct.
- `AnswerResultExtensions.ToShortString` / `Submission.Status` default / `CheatCheckInfo` — display/model only. ✔ unchanged.
- `ExerciseInstanceRepository` (practice/exercise mode, returns `AnswerResult.Accepted`) — a separate, non-game flow with no cheat concept; out of scope. ✔ unchanged.

**Frontend** (`src/GZCTF/ClientApp/src`)
- `components/GameChallengeModal.tsx` (player) — polls the `Status` endpoint and branches on `Accepted` / `WrongAnswer`. Because the controller now returns `Accepted` for a cheated solve, the player gets the normal "flag correct" path, the challenge is marked solved, and the optional dynamic-container auto-destroy runs — exactly like a legitimate solve. ✔ no change needed, verified.
- `pages/games/[id]/monitor/Submissions.tsx` (admin) — still renders the `CheatDetected` ("CD") badge for the stored status. ✔ unchanged (desired: admins still see it).
- `pages/games/[id]/monitor/Events.tsx` (admin) — still renders `CheatDetected` events. ✔ unchanged.
- `pages/games/[id]/monitor/CheatInfo.tsx` (admin) — still lists every `CheatInfo`. ✔ unchanged (see risks for the optional "was accepted" column that was skipped).

**Decision summary:** the single source of truth for "this team solved it" is the `FirstSolve` table, which the scoreboard already uses; nothing in the solved/scoring path keyed off `Submission.Status == Accepted`, so cheated solves (status `CheatDetected` + a `FirstSolve`) are correctly treated as solved everywhere. The only status-based reads remaining are the admin submission views (intended to keep showing `CheatDetected`) and the player status endpoint (now mapped to `Accepted`).

---

## 3. Frontend

No frontend source changes were required. The player-facing behaviour is driven entirely by the `Status` endpoint, which now returns `Accepted` for a shared-flag solve, so `GameChallengeModal.tsx` shows the normal success UI (including the dynamic-container auto-destroy). The admin monitor pages intentionally keep showing the `CheatDetected` status/event/record. Because no frontend file changed, the generated `Api.ts` client is still correct and no frontend rebuild was necessary.

The optional "show that the cheated submission was accepted/scored" enhancement to `monitor/CheatInfo.tsx` was **skipped**: it would require surfacing the FirstSolve/score join through the `CheatInfoModel` API, which is more than a trivial change. Admins can already see the cheat (CheatInfo page + `CheatDetected` submission) and the team's score on the scoreboard. Noted as a follow-up.

---

## 4. Test results

Backend build: **0 errors** (`dotnet build src/GZCTF/GZCTF.csproj -c Debug`).

New tests (`AcceptSharedFlagsTests`, run against a Postgres Testcontainer):

1. `SharedDynamicFlag_IsAccepted_AndRecordedAsCheat` — status endpoint returns `Accepted`; a `FirstSolve` exists for Team B; a `CheatInfo` exists with `SubmitTeam = B`, `SourceTeam = A`; stored submission status is `CheatDetected`. ✅
2. `CheatedSolve_DoesNotTakeBlood_LegitSolverGetsFirstBlood` — Team B cheats first, Team C solves legitimately; Team C is first blood, Team B is absent from the blood list; B's solve type is `Normal`, C's is `FirstBlood`. ✅
3. `CheatedSolve_AppearsOnScoreboard_WithNormalPoints` — B's cheated solve shows on the scoreboard with `Normal` type and the challenge's base dynamic score (no blood multiplier). ✅
4. `SharedFlag_AfterAlreadySolved_NoDuplicateFirstSolve_CheatStillRecorded` — exactly one `FirstSolve` for B, a `CheatInfo` for the second submission, stored status `CheatDetected`, player sees `Accepted`. ✅
5. `StaticChallenge_Behaviour_IsUnchanged` — correct (shared) static flag is a normal accept with no `CheatInfo`; wrong flag is `WrongAnswer`. ✅

`AcceptSharedFlagsTests`: **5 / 5 passed.**

- **Full integration suite** (`GZCTF.Integration.Test`, includes the 5 new tests plus the pre-existing `MultipleWrongSubmissions_ShouldNotTriggerCheatDetection`, scoreboard, and scoring tests): **173 / 173 passed, 0 failed.**
- **Unit test suite** (`GZCTF.Test`): **153 / 153 passed, 0 failed.**

Nothing in the existing suites regressed.

---

## 5. Risks, limitations, follow-ups

- **Static challenges remain undetectable.** Static flags are shared by design, so flag sharing on static challenges cannot be distinguished from a legitimate solve. Unchanged by this work, and out of scope.
- **Dynamic score inflation.** A cheated solve counts toward `solvedCount` (requirement 3), so it lowers the dynamic score for everyone, exactly like a real solve. This is intended but worth monitoring for games with many shared submissions.
- **FlagSubmit event value.** For a cheated solve the `FlagSubmit` game event now stores `"CheatDetected"` (the real status) instead of the old `"WrongAnswer"`. This is admin-only and more accurate; the separate `CheatDetected` event is unchanged.
- **Idempotency.** `CheatInfo` creation checks the unique `SubmissionId` before inserting, and the whole thing runs under the existing `pg_advisory_xact_lock(ParticipationId, ChallengeId)` inside one transaction, so concurrent/retried submissions cannot create duplicate `CheatInfo` or `FirstSolve` rows. A `DbUpdateConcurrencyException` still requeues the submission as before.
- **Admin CheatInfo page** does not yet indicate that a cheat was accepted/scored (skipped as non-trivial — see §3). Possible follow-up: add a flag to `CheatInfoModel`.
- **No new enum value and no DB migration** were needed. A cheated solve is identified as "accepted but cheated" by the existing pair (`FirstSolve.SubmissionId` + a `CheatInfo` with the same `SubmissionId`), exactly as requested.
- **Historical data.** Pre-existing cheat submissions (status `CheatDetected`, no `FirstSolve`) are untouched; they remain non-scored. Only new shared-flag submissions are accepted.

---

## 6. Building a custom Docker image from this branch

The repo's `src/GZCTF/Dockerfile` is a **thin packaging** Dockerfile: it expects the app to be **published first** into `publish/<platform>` and then copied in (that is how the project's multi-arch CI builds it). `dotnet publish` automatically builds the React frontend too (the `PublishFrontend` MSBuild target), so a single publish produces a complete app.

### Option A — use the repo's Dockerfile (matches upstream)

Prerequisites on your build machine: the .NET 10 SDK (`global.json` pins `10.0.401`) and Node + `pnpm` (or npm) for the frontend build, plus `docker buildx`.

```bash
# from the repo root, on the feature/accept-shared-flags branch

# 1. Publish the app (framework-dependent; frontend is built automatically).
#    The folder name must match the target platform used in step 2.
dotnet publish src/GZCTF/GZCTF.csproj -c Release -o src/GZCTF/publish/linux/amd64

# 2. Build the image. buildx sets TARGETPLATFORM, which the Dockerfile uses to
#    pick src/GZCTF/publish/linux/amd64. Build context is src/GZCTF.
docker buildx build \
  --platform linux/amd64 \
  -f src/GZCTF/Dockerfile \
  -t your-registry/gzctf:accept-shared-flags \
  --load \
  src/GZCTF
```

(For arm64, publish into `src/GZCTF/publish/linux/arm64` and use `--platform linux/arm64`. With classic `docker build` instead of buildx, add `--build-arg TARGETPLATFORM=linux/amd64`.)

### Option B — build entirely inside Docker (no local SDK/Node needed)

Save this as e.g. `Dockerfile.fullbuild` at the repo root and build with the repo root as context. It mirrors the upstream runtime stage but compiles from source:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Node + pnpm for the SPA build triggered by `dotnet publish`
RUN apt-get update && apt-get install -y --no-install-recommends nodejs npm \
    && npm install -g pnpm \
    && rm -rf /var/lib/apt/lists/*

COPY . .
RUN dotnet publish src/GZCTF/GZCTF.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8
WORKDIR /app
RUN apk add --update --no-cache wget libpcap icu-data-full icu-libs \
    ca-certificates libgdiplus tzdata krb5-libs && update-ca-certificates
COPY --from=build /publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "GZCTF.dll"]
```

```bash
docker build -f Dockerfile.fullbuild -t your-registry/gzctf:accept-shared-flags .
```

### Use it in your compose

Replace the image for the GZCTF service in your `compose.yml`:

```yaml
services:
  gzctf:
    # image: gztime/gzctf:latest
    image: your-registry/gzctf:accept-shared-flags
    # ...rest of your service config (ports, env, volumes, db, redis) unchanged
```

Then `docker compose up -d`. (If you tagged the image only locally with `--load`/`-t` and don't push to a registry, make sure compose runs on the same host, or `docker push` it first.)
