# Flag Acceptance & Cheat Detection — GZCTF Analysis

> **Scope:** Groundwork analysis for a future change that will make a **shared (cheated) flag be ACCEPTED** (submitter gets the solve/points) while the cheat is **still detected and recorded** for admins.
> **Status: analysis only — no source files were modified.** Only this file was created.
> All paths are relative to the repository root. Line numbers reflect the state of the tree at analysis time (branch `develop`).

---

## 1. Overview

### Player click → result, in words
1. The player enters a flag in the challenge modal and submits. The flag is **encrypted client-side** and POSTed to the game controller.
2. The controller validates (auth, participation, permission, deadline, submission limit), writes a `Submission` row with status `FlagSubmitted`, commits, and **queues** the submission on an in-process `Channel<Submission>`. It returns the **submission id** immediately (HTTP 200) — verification is asynchronous.
3. A background hosted service, `FlagChecker`, reads the channel with up to 4 workers and calls `GameInstanceRepository.VerifyAnswer`, which compares the submitted flag against the correct flag and (on first correct solve) records a `FirstSolve` and computes the blood tier.
4. If the answer is wrong, `FlagChecker` then calls `GameInstanceRepository.CheckCheat`, which looks for *another team's* dynamic flag matching the submitted answer. If found, it creates a `CheatInfo` record and sets the submission status to `CheatDetected`.
5. The final status is written to the `Submission` row and pushed to the **admin monitor** over SignalR (`MonitorHub`). Game events/notices are recorded.
6. The **frontend polls** `GET .../Status/{submitId}` every ~500ms. The controller **maps `CheatDetected` → `WrongAnswer`** before returning, so the submitter sees a cheated flag as simply *wrong*.

### Call flow
```
GameChallengeModal.onSubmit (frontend)
  → POST /api/Game/{id}/Challenges/{challengeId}            GameController.Submit
      → configService.DecryptApiData(flag)
      → GetContextInfo (auth / participation / game-state)
      → divisionRepository.GetPermission
      → submissionRepository.CountSubmissions (limit check)
      → submissionRepository.AddSubmission  (Status = FlagSubmitted)   [DB write]
      → channelWriter.WriteAsync(submission)                            [enqueue]
      → returns submission.Id (HTTP 200)

FlagChecker.Checker (IHostedService worker, reads Channel<Submission>)
  → GameInstanceRepository.VerifyAnswer(item)                           [DB: compare flag, FirstSolve, blood]
      ├─ Accepted  → GameEventRepository.AddEvent(FlagSubmit)
      │             → CacheHelper.FlushScoreboardCache
      └─ Wrong     → GameEventRepository.AddEvent(FlagSubmit)
                    → GameInstanceRepository.CheckCheat(item)           [DB: find other team's flag → CheatInfo]
                        └─ if match → Status=CheatDetected, AddEvent(CheatDetected)
  → (if blood) GameNoticeRepository.AddNotice
  → item.Status = ans; submissionRepository.SendSubmission(item)        [SignalR → MonitorHub]

Frontend polling: GET /api/Game/{id}/Challenges/{challengeId}/Status/{submitId}
  → GameController.Status  (maps CheatDetected → WrongAnswer)
```

### Mermaid sequence diagram
```mermaid
sequenceDiagram
    participant U as Player (GameChallengeModal)
    participant C as GameController
    participant DB as AppDbContext (Postgres)
    participant CH as Channel<Submission>
    participant F as FlagChecker worker
    participant GIR as GameInstanceRepository
    participant Cache as CacheHelper
    participant Hub as MonitorHub (SignalR)
    participant A as Admin monitor UI

    U->>C: POST Challenges/{cid} {flag (encrypted)}
    C->>C: Decrypt, validate (perm, deadline, limit)
    C->>DB: AddSubmission (Status=FlagSubmitted)
    C->>CH: WriteAsync(submission)
    C-->>U: 200 submissionId
    loop every ~500ms
        U->>C: GET Status/{submitId}
        C->>DB: GetSubmission
        C-->>U: status (CheatDetected mapped→WrongAnswer)
    end
    F->>CH: ReadAllAsync
    F->>GIR: VerifyAnswer(item)
    GIR->>DB: compare flag; advisory lock; FirstSolve; blood tier
    GIR-->>F: (SubmissionType, AnswerResult)
    alt Accepted
        F->>Cache: FlushScoreboardCache(gameId)
    else Wrong
        F->>GIR: CheckCheat(item)
        GIR->>DB: find other team's FlagContext==answer → CheatInfo; Status=CheatDetected
        GIR-->>F: CheatCheckInfo
    end
    F->>DB: item.Status = ans
    F->>Hub: SendSubmission(item)
    Hub-->>A: ReceivedSubmissions(submission)
```

---

## 2. Submission entry point

**File:** `src/GZCTF/Controllers/GameController.cs`
**Class:** `GameController` — **Method:** `Submit` — **Lines ~963–1056**
**Route:** `POST /api/Game/{id:int}/Challenges/{challengeId:int}`

```csharp
[RequireUser]
[HttpPost("{id:int}/Challenges/{challengeId:int}")]                 // line ~965
[EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Submit))]        // line ~966
public async Task<IActionResult> Submit(... FlagSubmitModel model, ...)
{
    var submitTime = DateTimeOffset.UtcNow;
    var answer = configService.DecryptApiData(model.Flag);          // flag arrives encrypted
    if (string.IsNullOrWhiteSpace(answer)) ... BadRequest           // ~974
    if (answer.Length > Limits.MaxFlagLength) ... BadRequest        // ~977
    var context = await GetContextInfo(id, token: token);           // auth/game/participation
    ...
}
```

### Validation performed before queuing
| Check | Line(s) | Behaviour on failure |
|---|---|---|
| Rate limiting (`Submit` policy) | ~966 (attribute) | 429 |
| Flag non-empty / length ≤ `Limits.MaxFlagLength` | ~975–978 | 400 |
| Game/participation context (`GetContextInfo`) | ~981 | its own result (401/400/404) |
| Instance exists for this participation+challenge (`GetInstanceForSubmission`) | ~991–995 | 404 |
| Deadline passed (unless `PracticeMode`) | ~998–1000 | 400 |
| Division permission `ViewChallenge | SubmitFlags` | ~1004–1007 | 400 |
| Submission count vs `Challenge.SubmissionLimit` | ~1010–1015 | 400 |

### How it is queued / how the frontend gets the result
- **Enqueue:** a new `Submission { Status = FlagSubmitted }` is persisted via `submissionRepository.AddSubmission`, the transaction is committed, then `await channelWriter.WriteAsync(submission, token)` pushes it onto the in-process `Channel<Submission>` (controller ctor injects `ChannelWriter<Submission>`, `GameController.cs:37`). The endpoint returns `submission.Id`. (Submit body ~1018–1037, with a 3-retry loop around `DbUpdateConcurrencyException`.)
- **Result delivery to the submitting player:** **polling**, not SignalR. `GET /api/Game/{id}/Challenges/{challengeId}/Status/{submitId}` → `GameController.Status` (**lines ~1071–1096**):
  ```csharp
  return Ok(submission.Status switch
  {
      AnswerResult.CheatDetected => AnswerResult.WrongAnswer,   // ~1092  ← key line
      var x => x
  });
  ```
- **Result delivery to admins:** SignalR. `SubmissionRepository.SendSubmission` (`src/GZCTF/Repositories/SubmissionRepository.cs:50-51`) broadcasts the full `Submission` (with real status, incl. `CheatDetected`) to `Game_{gameId}` group on `MonitorHub`.

The channel itself is registered in DI (search `Channel` / `BoundedChannel` in `src/GZCTF/Extensions` or `Program.cs`; `FlagChecker` is registered as the `IHostedService` that drains it). **UNVERIFIED:** exact registration line of the channel (not required for the planned change).

---

## 3. Flag verification logic

**File:** `src/GZCTF/Repositories/GameInstanceRepository.cs`
**Method:** `VerifyAnswer` — **Lines 262–390**

Core comparison (lines **289–303**):
```csharp
if (instance.FlagContext is null && challenge.Type.IsStatic())
{
    updateSub.Status = await Context.FlagContexts.AsNoTracking()
        .AnyAsync(f => f.ChallengeId == submission.ChallengeId && f.Flag == submission.Answer, token)
        ? AnswerResult.Accepted : AnswerResult.WrongAnswer;          // STATIC: any matching flag row
}
else
{
    updateSub.Status = instance.FlagContext?.Flag == submission.Answer
        ? AnswerResult.Accepted : AnswerResult.WrongAnswer;          // DYNAMIC: per-instance flag
}
```
If not `Accepted`, it commits and returns `(Unaccepted, status)` early (**lines 305–310**) — cheat detection happens *later* in `FlagChecker`, not here.

### Per challenge-type behaviour
`ChallengeType` (`src/GZCTF/Utils/Enums.cs:279-306`): `StaticAttachment=0b00`, `StaticContainer=0b01`, `DynamicAttachment=0b10`, `DynamicContainer=0b11`. `IsStatic() = (type & 0b10)==0` (`Enums.cs:307-320`).

| Type | `IsStatic()` | Where flag lives | Verification path |
|---|---|---|---|
| **StaticAttachment** | yes | `FlagContext` rows attached to the challenge (no per-instance flag; `instance.FlagContext` is null) | static branch: `FlagContexts.Any(f.ChallengeId==cid && f.Flag==answer)` |
| **StaticContainer** | yes | same (shared flag; container gets no per-team flag — see `CreateContainer` comment `GameInstanceRepository.cs:172`) | static branch, same as above |
| **DynamicAttachment** | no | a pre-generated `FlagContext` row **assigned to the instance** via `instance.FlagId` at dispatch | dynamic branch: `instance.FlagContext.Flag == answer` |
| **DynamicContainer** | no | per-team flag generated at container start, stored on `instance.FlagContext.Flag` | dynamic branch, same |

### Where per-team dynamic flags are generated / stored
**`GameInstanceRepository.GetInstance`** (`src/GZCTF/Repositories/GameInstanceRepository.cs:20-107`), in a transaction (lines **53–86**):
- **DynamicContainer** (lines 58–65): `instance.FlagContext = new FlagContext { Flag = challenge.GenerateDynamicFlag(part), IsOccupied = true }`.
- **DynamicAttachment** (lines 66–85): picks a random free `FlagContext` (`!IsOccupied`), marks it occupied, sets `instance.FlagId`.
- Static: no dispatch (`instance.FlagContext` stays null; comment lines 88–89).

Flag generation: `Challenge.GenerateDynamicFlag(Participation part)` (`src/GZCTF/Models/Data/Challenge.cs:116-128`) — team-specific hash from `TeamHashSalt::ChallengeId` and `part.Token`.

**Models/tables involved:** `FlagContext` (`src/GZCTF/Models/Data/FlagContext.cs` — `Flag`, `IsOccupied`, `ChallengeId`, `AttachmentId`), `GameInstance` (`src/GZCTF/Models/Data/GameInstance.cs` — PK `(ParticipationId, ChallengeId)`, `FlagId`, nav `FlagContext`).

---

## 4. Cheat / flag-sharing detection

**File:** `src/GZCTF/Repositories/GameInstanceRepository.cs`
**Method:** `CheckCheat` — **Lines 233–260**

```csharp
var instance = await Context.GameInstances
    .Include(i => i.Participation).ThenInclude(i => i.Team)
    .Include(i => i.FlagContext)
    .Where(i => i.ChallengeId == submission.ChallengeId &&
                i.ParticipationId != submission.ParticipationId &&   // another team...
                i.FlagContext != null &&
                i.FlagContext.Flag == submission.Answer)             // ...whose dynamic flag == answer
    .FirstOrDefaultAsync(token);                                     // lines 237-244

if (instance is null) return checkInfo;                              // AnswerResult defaults to WrongAnswer

var updateSub = await Context.Submissions.Where(s => s.Id == submission.Id).SingleAsync(token);
var cheatInfo = await cheatInfoRepository.CreateCheatInfo(updateSub, instance, token); // line 251
checkInfo = CheatCheckInfo.FromCheatInfo(cheatInfo);
updateSub.Status = AnswerResult.CheatDetected;                       // line 255  ← status flip
await SaveAsync(token);
return checkInfo;
```

### Trigger condition
A **wrong** submission whose answer exactly matches the **per-instance `FlagContext.Flag` of a *different* participation** on the **same challenge**. Because it matches on `FlagContext.Flag`, this only works for **dynamic** challenges (static challenges have no per-instance `FlagContext`).

### What happens when cheating is detected
- **DB — `CheatInfo` row** created in `CheatInfoRepository.CreateCheatInfo` (`src/GZCTF/Repositories/CheatInfoRepository.cs:10-30`): `{ GameId, Submission = submitter's submission, SubmitTeam = submitter's participation, SourceTeam = flag owner's participation }`. (`CheatInfo` model: `src/GZCTF/Models/Data/CheatInfo.cs` — unique index on `SubmissionId`.)
- **DB — submission status** set to `AnswerResult.CheatDetected` (`GameInstanceRepository.cs:255`, persisted; and again written by `FlagChecker` at `item.Status = ans`).
- **Event** `EventType.CheatDetected` recorded in `FlagChecker.Checker` (`src/GZCTF/Services/FlagChecker.cs:142-161`) with `[challenge, submitTeam, sourceTeam]`, tied to the **submitter's** `TeamId/UserId`. (A `FlagSubmit` event is also recorded first, lines 136–137.)
- **Log** `FlagChecker_CheatDetected` at Information level (`FlagChecker.cs:144-149`).
- **SignalR** — the submission (status `CheatDetected`) is pushed to the admin monitor via `SendSubmission` (`FlagChecker.cs:174`).
- **Game notice?** No. A blood/solve notice is only added when `type` is a blood tier (`FlagChecker.cs:167-171`, `type != Unaccepted && type != Normal`); a cheat is `Unaccepted`, so **no public notice**.
- **Affected team:** only the **submitter's** team is recorded/flagged. The **flag owner's** team (`SourceTeam`) is referenced in the `CheatInfo` but **not penalized or marked** anywhere. No automatic ban/suspension occurs.

### Where the "cheat = NOT accepted" decision is made (precise)
1. **`GameInstanceRepository.VerifyAnswer`** — a shared dynamic flag never equals *this* team's `instance.FlagContext.Flag`, so it returns `WrongAnswer` at **`GameInstanceRepository.cs:300-303`**, taking the early non-accepted return at **lines 305–310**. No `FirstSolve`, no scoring.
2. **`FlagChecker.Checker`** — cheat handling is inside the `default:` (non-accepted) branch (**`FlagChecker.cs:127-164`**); the `Accepted` branch (scoreboard flush, solve event) is skipped. Final status written is `CheatDetected` (**line 173**).
3. **`GameController.Status`** — `CheatDetected` is mapped to `WrongAnswer` before returning to the player (**`GameController.cs:1092`**), so the submitter is told the flag is wrong.

### Limitations
- **No detection for static challenges** — cheat matching keys on per-instance `FlagContext.Flag`, which static types don't have. Sharing a static flag is indistinguishable from a legitimate solve.
- Detection is **best-effort / post-hoc**: it only fires when `VerifyAnswer` already returned non-accepted, and only if the exact other-team flag still exists in `GameInstances`.

---

## 5. Result statuses

### `AnswerResult` — `src/GZCTF/Utils/Enums.cs:515-541` (also frontend `src/GZCTF/ClientApp/src/Api.ts:25-31`)
| Value | Num | Meaning |
|---|---|---|
| `FlagSubmitted` | 0 | Queued, not yet judged |
| `Accepted` | 1 | Correct |
| `WrongAnswer` | 2 | Incorrect |
| `CheatDetected` | 3 | Shared/other team's flag detected |
| `NotFound` | -1 | No matching challenge instance |

### `SubmissionType` — `src/GZCTF/Utils/Enums.cs:222-249` (frontend `Api.ts:43-49`)
`Unaccepted=0`, `FirstBlood=1`, `SecondBlood=2`, `ThirdBlood=3`, `Normal=4`. Returned by `VerifyAnswer` (not persisted on the submission; drives events/notices/scoreboard tiering).

### Every place that reads/branches on these statuses

**Backend:**
- `GameInstanceRepository.VerifyAnswer` sets `Accepted`/`WrongAnswer`/`NotFound` (`GameInstanceRepository.cs:275, 291-303, 305`).
- `GameInstanceRepository.CheckCheat` sets `CheatDetected` (`GameInstanceRepository.cs:255`); `CheatCheckInfo` defaults `WrongAnswer` (`src/GZCTF/Models/Internal/CheatCheckInfo.cs:8`, `:34`).
- `FlagChecker.Checker` switches on `AnswerResult` (`FlagChecker.cs:101-165`): `NotFound`, `Accepted`, `default` (wrong→cheat).
- `GameController.Status` maps `CheatDetected`→`WrongAnswer` (`GameController.cs:1091-1095`).
- `AnswerResultExtensions.ToShortString` localizes each value (`Enums.cs:543-560`) → resource keys `Submission_FlagSubmitted/Accepted/WrongAnswer/CheatDetected/UnknownInstance` (all `.resx` under `src/GZCTF/Resources/Program*.resx`, e.g. `Program.resx:905` for `Submission_CheatDetected`).
- `GameEvent.FromSubmission` stores `ans.ToString()` in event values (`src/GZCTF/Models/Data/GameEvent.cs:55-70`).
- `SubmissionRepository.GetSubmissionsByType` / `GetSubmissions` filter by `AnswerResult` (`src/GZCTF/Repositories/SubmissionRepository.cs:36-61`), used by `GameController.Submissions` (`/{id}/Submissions?type=`, `GameController.cs:451-466`) and the Excel export.
- `GetUncheckedFlags` requeues `FlagSubmitted` on startup (`SubmissionRepository.cs:32-34`).

**Frontend:**
- `components/GameChallengeModal.tsx`: polls `gameStatus`; branches `Accepted` / `WrongAnswer` / else-unknown in `checkDataFlag` (**lines ~107, 122, 138**) and treats `!== FlagSubmitted` as "done" (**line ~224**). Because the controller masks `CheatDetected`, the player path only ever sees `Accepted`/`WrongAnswer`/`NotFound`.
- `pages/games/[id]/monitor/Submissions.tsx` and `.../Events.tsx`: render `AnswerResult`/`EventType` (incl. `CheatDetected`) for admins.
- `pages/games/[id]/monitor/CheatInfo.tsx`: dedicated cheat view (section 7).

**Player-facing notification strings** (`src/GZCTF/ClientApp/src/locales/<lang>/challenge.json`, key `challenge.notification.flag.*`): `accepted.{title,message,ended}`, `wrong`, `submitted.*`, `unknown.*`. There is **no** "cheat" string on the player side (by design). Admin-side cheat strings live in `locales/<lang>/game.json` (`cheat_detected`, `cheat_info.*`, `tabs...cheatinfo`).

---

## 6. What "accepted" triggers

All in `GameInstanceRepository.VerifyAnswer` (`src/GZCTF/Repositories/GameInstanceRepository.cs`) once status is `Accepted` (lines **312–381**), plus `FlagChecker`:

- **Solve record:** inserts a `FirstSolve { ParticipationId, ChallengeId, SubmissionId }` (**lines 371–376**). Table `FirstSolve` (`src/GZCTF/Models/Data/FirstSolve.cs`): PK `(ParticipationId, ChallengeId)`, unique `SubmissionId` — this is the **single source of truth** for scoring. The `Submission.Status` becomes `Accepted` but scoreboard reads **only** `FirstSolves`.
- **Blood tier:** `hasBloodPermission` requires within game window + within deadline + `!DisableBloodBonus` + division `GetBlood` permission (**lines 344–354**). Then `CountBloodEligibleSolves` (**lines 392–422**) counts existing eligible first solves → `0→FirstBlood, 1→SecondBlood, 2→ThirdBlood, else Normal` (**lines 362–368**).
- **Dynamic score + rankings (scoreboard):** computed lazily in `GameRepository.GenScoreboard` (`src/GZCTF/Repositories/GameRepository.cs:280+`). It reads `FirstSolves` joined to `Submissions`/`Participations` (**lines 372–389**), computes per-challenge current score via `GameChallenge.CalculateChallengeScore(originalScore, minScoreRate, difficulty, solvedCount)` (`GameRepository.cs:445-446`; `GameChallenge.cs:49`), applies blood factors from `game.BloodBonus` (first/second/third factors, `GameRepository.cs:449-456`, tiering ~479–513), sums per team, sorts by score then last-submission time (~537).
- **Scoreboard caching:** `GameRepository.GetScoreboard` uses `CacheHelper.GetOrCreateAsync(CacheKey.ScoreBoard(gameId), ...)` (`GameRepository.cs:162-168`). **Invalidation:** `FlagChecker` calls `cacheHelper.FlushScoreboardCache(item.GameId)` on every `Accepted` (`FlagChecker.cs:124`). `CacheHelper.FlushScoreboardCache` → `src/GZCTF/Services/Cache/CacheHelper.cs:70`; key `_ScoreBoard_{id}` (`CacheHelper.cs:247`).
- **Events / notices:** `GameEvent FlagSubmit` always on accepted (`FlagChecker.cs:120-121`). A **public `GameNotice`** (`FirstBlood/SecondBlood/ThirdBlood`) only for blood tiers (`FlagChecker.cs:167-171` → `GameNotice.FromSubmission`, `src/GZCTF/Models/Data/GameNotice.cs:35-48`).
- **Live push:** `SubmissionRepository.SendSubmission` → `MonitorHub` admin feed (`FlagChecker.cs:174`).
- **Container side effects:** none from the solve itself. (Frontend *optionally* auto-destroys a dynamic container after an accepted flag: `GameChallengeModal.tsx` `requestDestroy()` in `checkDataFlag`.)
- **Double-solve / re-submission prevention:**
  - **Postgres advisory xact lock** `pg_advisory_xact_lock(ParticipationId, ChallengeId)` (`GameInstanceRepository.cs:317-319`) serializes concurrent solves of the same pair.
  - **`alreadySolved`** check against `FirstSolves` (**lines 321–330**): if present, returns `(Normal, Accepted)` **without** inserting a second `FirstSolve` — so re-submitting a correct flag is accepted but awards nothing new.
  - `FirstSolve` PK + unique `SubmissionId` enforce one solve per (participation, challenge) at the DB level.

---

## 7. Admin side

- **API:** `GET /api/Game/{id:int}/CheatInfo` — `GameController.CheatInfo` (`src/GZCTF/Controllers/GameController.cs:480-496`), `[RequireMonitor]`. Returns `CheatInfoModel[]` (only after game start). Data source: `CheatInfoRepository.GetCheatInfoByGameId` (`src/GZCTF/Repositories/CheatInfoRepository.cs:32-38`) — includes `SourceTeam.Team`, `SubmitTeam.Team`, `Submission.User`, `Submission.GameChallenge`.
- **DTO:** `CheatInfoModel` (`src/GZCTF/Models/Request/Game/CheatInfoModel.cs`): `OwnedTeam` (flag owner), `SubmitTeam` (cheater), `Submission` (full, incl. answer, user, time, challenge).
- **Frontend:** `src/GZCTF/ClientApp/src/pages/games/[id]/monitor/CheatInfo.tsx` — groups cheating by team, shows submitter team, owner team, flag, user, challenge, time (table view `CheatInfoTableView`). Admins also see cheat submissions in `monitor/Submissions.tsx` and `CheatDetected` events in `monitor/Events.tsx`.
- **Existing admin actions on cheat records:** **none dedicated.** The CheatInfo page is read-only (no ban/ignore/delete). Team suspension exists separately (participation status → `Suspended`, via admin/edit controllers) but is **not** wired to cheat records. **UNVERIFIED:** no cheat-triggered auto-ban exists in the submission path (confirmed absent in `FlagChecker`/`GameInstanceRepository`).

---

## 8. Data model reference

| Entity | File | Key fields | Relationships |
|---|---|---|---|
| `Submission` | `src/GZCTF/Models/Data/Submission.cs` | `Id`, `Answer`, `Status: AnswerResult`, `SubmitTimeUtc` | → `User`, `Team`, `Participation`, `Game`, `GameChallenge` |
| `CheatInfo` | `src/GZCTF/Models/Data/CheatInfo.cs` | `GameId`, `SubmitTeamId`, `SourceTeamId`, `SubmissionId` (unique) | `SubmitTeam`/`SourceTeam`: `Participation`; `Submission` |
| `FirstSolve` | `src/GZCTF/Models/Data/FirstSolve.cs` | PK `(ParticipationId, ChallengeId)`, `SubmissionId` (unique) | → `Participation`, `Challenge`, `Submission` — **scoring source of truth** |
| `GameInstance` | `src/GZCTF/Models/Data/GameInstance.cs` | PK `(ParticipationId, ChallengeId)`, `FlagId`, `IsLoaded` | → `Challenge`, `Participation`, `FlagContext`, `Container` |
| `FlagContext` | `src/GZCTF/Models/Data/FlagContext.cs` | `Flag`, `IsOccupied`, `ChallengeId`, `AttachmentId` | → `Challenge`, `Attachment` |
| `Participation` | `src/GZCTF/Models/Data/Participation.cs` | `Id`, `Status`, `GameId`, `TeamId`, `DivisionId`, `Token` | → `Team`, `Game`, `Division`, instances |
| `GameChallenge` | `src/GZCTF/Models/Data/GameChallenge.cs` | `Type`, `OriginalScore`, `MinScoreRate`, `Difficulty`, `DisableBloodBonus`, `DeadlineUtc`, `SubmissionLimit` | → `Game`, flags, instances |
| `GameEvent` | `src/GZCTF/Models/Data/GameEvent.cs` | `Type: EventType`, `Values[]` | → team/user/game |
| `GameNotice` | `src/GZCTF/Models/Data/GameNotice.cs` | `Type: NoticeType`, `Values[]` | → game (public) |

**EF Core migrations:** folder `src/GZCTF/Migrations/` (snapshot `AppDbContextModelSnapshot.cs`; latest `20251203143253_AddNetworkMode`, and `20251020175612_AddFirstSolves`). Design-time factory: `src/GZCTF/Models/DesignTimeAppDbContextFactory.cs`; DbContext `src/GZCTF/Models/AppDbContext.cs`. **Create a migration** with `dotnet ef migrations add <Name>` from the `src/GZCTF` project (standard EF Core CLI; applied automatically at startup — **UNVERIFIED:** confirm auto-`Migrate()` in `Program.cs`/`Server.cs` before relying on it).

---

## 9. Candidate modification points (analysis only — do not edit)

**Goal:** a shared dynamic flag should be **accepted (solve + points for the submitter)** *and* still **recorded as a cheat** for admins.

| # | File · method · lines | Change (one-liner) |
|---|---|---|
| 1 | `src/GZCTF/Services/FlagChecker.cs` · `Checker` · 127–164 | Cheat is currently only evaluated in the *non-accepted* branch. Need a flow where a wrong-but-cheated flag is (optionally) **promoted to accepted** (solve recorded) *while* `CheatInfo` is still created — i.e. call cheat detection, then run the accept path (FirstSolve, blood, scoreboard flush, events). |
| 2 | `src/GZCTF/Repositories/GameInstanceRepository.cs` · `CheckCheat` · 233–260 | Today it sets `updateSub.Status = CheatDetected` (line 255). To accept, it must **create the `CheatInfo` but not force a reject status** (or the caller must override). Decide final `Submission.Status` semantics. |
| 3 | `src/GZCTF/Repositories/GameInstanceRepository.cs` · `VerifyAnswer` · 289–310 | A shared flag matches *another* team's `FlagContext`, so it returns `WrongAnswer` and returns early before `FirstSolve`/blood. To grant a solve, acceptance logic must recognize "answer matches *some* team's flag for this challenge" (not just this team's) — or acceptance must be driven from the cheat path (option 1). This is the **central comparison** that currently blocks the solve. |
| 4 | `src/GZCTF/Repositories/GameInstanceRepository.cs` · `VerifyAnswer` · 371–376 | The `FirstSolve` insert (solve + scoreboard eligibility) must run for an accepted-cheat. Reuse the advisory lock (317) + `alreadySolved` guard (321–330) so a cheated solve can't double-insert. |
| 5 | `src/GZCTF/Controllers/GameController.cs` · `Status` · 1091–1095 | Currently maps `CheatDetected`→`WrongAnswer`. If the submitter should now see **Accepted**, change/remove this mapping (and decide the player-facing status for a flag that is accepted *and* cheated). |
| 6 | `src/GZCTF/Utils/Enums.cs` · `AnswerResult` · 515–541 **(+ `Api.ts:25`)** | Possibly add a status like `AcceptedCheat` (accepted + flagged) if you want to distinguish it from a clean accept without losing the cheat marker on the submission. Requires regenerating the frontend `Api.ts` enum + localization keys. |
| 7 | `src/GZCTF/Services/FlagChecker.cs` · 167–171 / `GameNotice.FromSubmission` | If accepted-cheat should produce blood/solve notices, ensure the `type` returned is a real `SubmissionType` (not `Unaccepted`) so the notice path fires. Decide whether a cheater can take blood. |
| 8 | `src/GZCTF/Repositories/GameRepository.cs` · `GenScoreboard` · 372–513 | Scoreboard reads `FirstSolves` and applies blood factors; once an accepted-cheat has a `FirstSolve`, it will score & potentially take blood automatically. Decide whether cheated solves should be excluded from blood or dynamic-score counts (would need a flag on `FirstSolve`/join filter here). |
| 9 | `src/GZCTF/Models/Data/CheatInfo.cs` + new migration under `src/GZCTF/Migrations/` | If you want to mark cheated-but-accepted solves (e.g. an `Accepted`/`Resolved` flag, or link `CheatInfo`↔`FirstSolve`), add columns + migration. |
| 10 | `src/GZCTF/ClientApp/src/components/GameChallengeModal.tsx` · `checkDataFlag` · 107–145 | If the submitter should be told anything (e.g. "accepted"), adjust branches; today only `Accepted`/`WrongAnswer` are handled. |
| 11 | `src/GZCTF/ClientApp/src/pages/games/[id]/monitor/CheatInfo.tsx` | Admin view may need to show that a cheat was *accepted/scored* (new column), plus any new admin action (revoke solve / ban). |

### Side effects & risks to weigh
- **Blood bonus for cheaters:** with a `FirstSolve` inserted, `GenScoreboard` will award blood if eligible (`GameRepository.cs:479-513`). Likely you want cheated solves to score but **not** take first/second/third blood — needs an explicit exclusion.
- **Dynamic score inflation:** each accepted solve increments `solvedCount` → lowers everyone's dynamic score (`CalculateChallengeScore`). Accepting cheats changes the curve for all teams.
- **Flag owner's team:** currently untouched. Decide if the source team should be marked/penalized when its flag is accepted elsewhere.
- **Existing cheat records:** historical `CheatInfo` rows have `Submission.Status = CheatDetected` and **no** `FirstSolve`. A migration/backfill decision is needed if semantics change.
- **UI honesty:** telling the submitter "Accepted" while silently recording a cheat is a product decision (anti-cheat value of *not* revealing detection vs. transparency).
- **Race conditions:** keep the `pg_advisory_xact_lock` (`GameInstanceRepository.cs:317`) and `alreadySolved` guard; the cheat path (`CheckCheat`) currently runs **outside** that lock and in a **separate** scope from `VerifyAnswer` — merging accept+cheat must preserve single-solve guarantees and avoid duplicate `FirstSolve`/`CheatInfo` under concurrency.
- **Startup requeue:** `GetUncheckedFlags` only requeues `FlagSubmitted`; any new intermediate status must not strand submissions.

### Tests covering this logic
- `src/GZCTF.Integration.Test/Tests/Api/GameWorkflowTests.cs` — includes `MultipleWrongSubmissions_ShouldNotTriggerCheatDetection` (line ~541) — the closest existing cheat-related test.
- `src/GZCTF.Integration.Test/Tests/Api/ScoreboardCalculationTests.cs`, `DetailedGameScoringTests.cs`, `AdvancedGameMechanicsTests.cs` — scoring/blood.
- `src/GZCTF.Integration.Test/Tests/Repository/SubmissionRepositoryTests.cs` — submissions.
- `src/GZCTF.Integration.Test/Tests/Api/PracticeModeDeadlineTests.cs` — deadline/accept edge cases.
- **New tests needed:** "shared dynamic flag is accepted AND a `CheatInfo` is created", blood-exclusion-for-cheater, no-double-solve under concurrency, scoreboard reflects the accepted-cheat solve, admin CheatInfo still lists it.

---

## Open questions
1. **Player feedback:** should a cheating submitter see `Accepted`, or still `WrongAnswer` while being scored silently (anti-cheat)? This decides whether to touch `GameController.Status:1091-1095`.
2. **Blood bonus:** can a cheated/shared solve take first/second/third blood, or only base/dynamic points?
3. **Flag owner (source team):** marked, penalized, or untouched when their flag is accepted for another team?
4. **New status vs. reuse:** add `AnswerResult.AcceptedCheat` (clear audit, but enum/API/i18n churn) or keep `Accepted` on the submission and rely solely on the `CheatInfo` row to mark it?
5. **Static challenges:** out of scope (undetectable), or do we also want sharing handling there (would need a different detection mechanism)?
6. **Migration/backfill:** leave historical `CheatDetected` submissions as-is, or reclassify?
7. **Admin actions:** add revoke-solve / ban-from-cheat on the CheatInfo page as part of this change?
8. **Concurrency model:** fold `CheckCheat` into the same transaction/advisory-lock as `VerifyAnswer`, or keep separate and reconcile status afterward?
```
