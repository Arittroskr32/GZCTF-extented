# GZCTF Discord Integration

Branch: `feature/discord-notifications` (based on `feature/accept-shared-flags`).

Posts two kinds of notifications to Discord:

1. **First Blood** for a challenge → a **public** channel.
2. **Flag sharing / cheat detected** → a **private** (admin-only) channel.

The two channels are configured independently and may be the same channel id. Everything is driven by an
external `discord.yml`; if it is missing or disabled, GZCTF runs exactly as before. All Discord work runs
off the flag-checking hot path (an in-process queue drained by a background service) and can never slow
down or break flag submission.

---

## 1. Files added / changed

### Added — `src/GZCTF/Discord/` (namespace `GZCTF.Discord`)
| File | Purpose |
|---|---|
| `DiscordOptions.cs` | Raw YAML shape (`enabled`, `bot_token`, `first_blood`, `cheat_detection`, `games`). |
| `DiscordConfig.cs` | Validated runtime config (channel ids as `ulong`, enabled parts, games set, `ShouldNotifyGame`, de-duplicated `ActiveChannelIds`). |
| `DiscordConfigLoader.cs` | Loads/validates `discord.yml`; resolves path from `GZCTF_DISCORD_CONFIG` (default `/app/discord.yml`); never throws, never logs the token; disables only broken parts. |
| `DiscordNotification.cs` | Lightweight queue records: `FirstBloodNotification`, `CheatNotification`. |
| `IDiscordNotifier.cs` | Fire-and-forget hook API used by `FlagChecker`. |
| `NullDiscordNotifier.cs` | No-op used when the integration is disabled, so hook points are unconditional. |
| `DiscordNotifier.cs` | Enqueues onto the bounded channel; applies the enabled/blood-tier/games filters; drops with a warning if the queue is full. |
| `DiscordMessage.cs` | JSON payload models for the REST API; `allowed_mentions.parse` is always `[]`. |
| `DiscordEmbedFactory.cs` | Builds the first-blood and cheat embeds; markdown escaping, truncation to Discord limits, `<t:unix:F>` timestamps, per-tier titles/colours. |
| `DiscordApiClient.cs` | Typed `HttpClient` wrapper: `POST /channels/{id}/messages`, `GET /channels/{id}`; honours 429 `retry_after`, retries 5xx/network with backoff, never throws into the caller. |
| `DiscordNotificationService.cs` | `BackgroundService` draining the queue; loads game/challenge/team details off the hot path; verifies channel access on startup. |
| `DiscordServiceExtensions.cs` | `AddDiscordIntegration()` DI wiring: loads config, registers queue + notifier + typed client + hosted service, or just `NullDiscordNotifier` when disabled. |

### Added — repo root & tests
| File | Purpose |
|---|---|
| `discord.example.yml` | Documented example config (copy to `discord.yml`). |
| `src/GZCTF.Test/UnitTests/Discord/DiscordConfigLoaderTests.cs` | Config loading/validation tests. |
| `src/GZCTF.Test/UnitTests/Discord/DiscordEmbedFactoryTests.cs` | Embed escaping/truncation/allowed-mentions/flag-visibility tests. |
| `src/GZCTF.Test/UnitTests/Discord/DiscordApiClientTests.cs` | HTTP sender tests with a mocked handler (429/5xx/network/allowed_mentions). |
| `src/GZCTF.Test/UnitTests/Discord/DiscordNotifierTests.cs` | Enqueue/filter/queue-full tests. |
| `src/GZCTF.Integration.Test/Tests/Api/DiscordNotificationHookTests.cs` | End-to-end hook tests through the real flag-checking pipeline. |

### Changed (minimal, hook points + wiring only)
| File | Change |
|---|---|
| `src/Directory.Packages.props` | Added `YamlDotNet` 16.3.0 (central package management). |
| `src/GZCTF/GZCTF.csproj` | Added `<PackageReference Include="YamlDotNet"/>`. |
| `src/GZCTF/Extensions/Startup/ServicesExtension.cs` | `using GZCTF.Discord;` and one line: `builder.Services.AddDiscordIntegration();`. |
| `src/GZCTF/Services/FlagChecker.cs` | Inject `IDiscordNotifier`; enqueue a cheat alert where the `CheatDetected` event is recorded; enqueue a blood alert where the blood `GameNotice` is created (same final tier). |
| `.gitignore` | Ignore `discord.yml` / `**/discord.yml` (never commit the real token file). |

### Hook points (why they are correct)
- **First blood:** enqueued inside the exact `if` block that creates the `FirstBlood`/`SecondBlood`/`ThirdBlood`
  `GameNotice` in `FlagChecker`, using the same final `SubmissionType`. So the Discord message always agrees
  with the in-game notice. On this branch a cheated solve is `SubmissionType.Normal` and never enters that
  block, so a cheat can never produce a first-blood message. (Verified by `SharedFlagSolve_…_AndNoFirstBlood`.)
- **Cheat detected:** enqueued where the `CheatDetected` game event is recorded (the accepted+cheat path).
  Each submission is detected exactly once there, so each submission triggers at most one cheat alert.
- The `games` allow-list is applied in the notifier before enqueuing. Game/challenge/team lookups happen in
  the background service, not in `FlagChecker`.

---

## 2. `discord.yml` reference

```yaml
enabled: true                 # master switch for the whole integration
bot_token: "YOUR_BOT_TOKEN"   # Discord bot token; kept secret, never logged

first_blood:
  enabled: true               # post first-blood announcements
  channel_id: "123456789012345678"   # PUBLIC channel id (numeric snowflake, as a string)
  include_second_third_blood: false  # also post 2nd/3rd blood (distinct title/colour). default false

cheat_detection:
  enabled: true               # post cheat (shared-flag) alerts
  channel_id: "234567890123456789"   # PRIVATE channel id (numeric snowflake, as a string)
  show_submitted_flag: true   # include the shared flag value in the private alert. default false

games: []                     # optional allow-list of game IDs; empty = all games
```

| Key | Meaning |
|---|---|
| `enabled` | If `false` (or file missing), the whole integration is silently disabled. |
| `bot_token` | Bot token from the Developer Portal. Required; if absent the integration is disabled. Never logged. |
| `first_blood.enabled` | Turn first-blood announcements on/off. |
| `first_blood.channel_id` | Public channel id as a numeric string (17–20 digits). Invalid → first-blood disabled, rest still works. |
| `first_blood.include_second_third_blood` | Also announce 2nd/3rd blood with distinct titles/colours. |
| `cheat_detection.enabled` | Turn cheat alerts on/off. |
| `cheat_detection.channel_id` | Private channel id as a numeric string. Invalid → cheat alerts disabled, rest still works. |
| `cheat_detection.show_submitted_flag` | Include the submitted (shared) flag value in the private alert. |
| `games` | List of game IDs to notify for. Empty list means all games. |

Channel ids may be identical for both sections; the integration de-duplicates them for the startup access
check and works correctly either way.

---

## 3. Setup

### 3.1 Create the bot
1. Go to the **Discord Developer Portal** → **New Application** → name it (e.g. "GZCTF").
2. Open the **Bot** tab → **Reset Token** → copy the token into `bot_token`. Keep it secret.
3. No privileged gateway intents are required (the integration only uses the REST API).

### 3.2 Invite the bot with the right permissions
The bot needs, in each target channel: **View Channel**, **Send Messages**, **Embed Links**.

Build an OAuth2 invite URL (Developer Portal → **OAuth2 → URL Generator**):
- Scopes: `bot`
- Bot Permissions: **View Channel**, **Send Messages**, **Embed Links**
- Open the generated URL and add the bot to your server.

### 3.3 Copy the channel IDs
1. Discord → **User Settings → Advanced → Developer Mode: ON**.
2. Right-click the **public** channel → **Copy Channel ID** → `first_blood.channel_id`.
3. Right-click the **private** channel → **Copy Channel ID** → `cheat_detection.channel_id`.

### 3.4 Make the cheat channel private (admin-only)
1. Create a role, e.g. `@Staff`, and assign it to your admins.
2. Create (or edit) the cheat channel → **Edit Channel → Permissions**.
3. For `@everyone`: **deny** *View Channel*.
4. Add `@Staff`: **allow** *View Channel*.
5. Add the **bot** (its role or the bot user): **allow** *View Channel*, *Send Messages*, *Embed Links*.

A common pattern is a private **category** (deny `@everyone` View Channel, allow `@Staff` + bot) with the
cheat channel inheriting those permissions. The public first-blood channel just needs the bot able to post.

---

## 4. Docker / compose

The custom image is built from this branch (see `flag-acceptance-changes.md` §6 for the two build recipes).
Mount the config read-only into the container:

```yaml
services:
  gzctf:
    image: your-registry/gzctf:discord        # your custom image built from this branch
    restart: always
    ports:
      - "8080:8080"
    volumes:
      - "./appsettings.json:/app/appsettings.json:ro"
      - "./discord.yml:/app/discord.yml:ro"    # <-- add this line
      - "./files:/app/files"
    # ...your existing db / redis / env config unchanged
```

- Default config path inside the container is `/app/discord.yml`. To use another path, set
  `GZCTF_DISCORD_CONFIG` (e.g. `environment: { GZCTF_DISCORD_CONFIG: /config/discord.yml }`).
- Keep `discord.yml` next to your compose file. It is git-ignored; never commit it.

### Verify it works (startup log lines)
On boot you will see one of:

- Disabled / missing file:
  - `[Discord] No config file at /app/discord.yml; Discord integration disabled`
  - `[Discord] Integration is disabled via config (/app/discord.yml)`
- Enabled:
  - `[Discord] First blood notifications enabled (channel 123456789012345678)`
  - `[Discord] Cheat notifications enabled (channel 234567890123456789)`
  - `[Discord] Channel 123456789012345678 is accessible`
- Problems (integration keeps running; only the broken part is disabled):
  - `[Discord] first_blood.channel_id is not a valid Discord channel id; first blood notifications disabled`
  - `[Discord] bot_token is missing; Discord integration disabled`
  - `[Discord] Cannot use channel 234567890123456789: missing access (grant the bot View Channel / Send Messages / Embed Links)`
  - `[Discord] Cannot use channel 234567890123456789: channel not found (wrong channel id, or bot not in the server)`

A live smoke test: solve a challenge first in a game → a first-blood embed appears in the public channel;
submit another team's dynamic flag → a warning embed appears in the private channel (and the submitter still
sees the flag as accepted).

---

## 5. Security & reliability notes

- **No pings:** every message sets `allowed_mentions: { "parse": [] }`, so team names / usernames / flags can
  never ping `@everyone`, roles or users.
- **Markdown-safe:** all user-controlled text (team names, usernames, challenge titles, flags) is
  markdown-escaped and truncated to Discord's embed limits; the flag is additionally wrapped in an inline
  code span and only included when `show_submitted_flag: true`.
- **Non-blocking:** the hook points only enqueue onto a bounded in-process channel (capacity 1000). If the
  queue is full the message is dropped with a warning — flag checking is never blocked.
- **Resilient:** the sender honours 429 `retry_after`, retries 5xx/network errors with exponential backoff,
  and after the final attempt logs and drops the message. It never throws into the flag checker.
- **Token safety:** the bot token is only used to set the HTTP `Authorization` header; it is never written to
  any log line.

---

## 6. Test results

- **Unit tests** (`GZCTF.Test`): **192 passed, 0 failed** — includes **39 new Discord tests**:
  - `DiscordConfigLoaderTests`: missing file → disabled; `enabled: false` → disabled; missing token → disabled;
    invalid channel id disables only that part; same channel id for both works (deduped); full config parses;
    snowflake validation.
  - `DiscordEmbedFactoryTests`: markdown escaping; truncation; distinct titles/colours per blood tier; flag
    omitted when `show_submitted_flag: false` and present when `true`; `allowed_mentions` always empty;
    `<t:unix:F>` timestamp.
  - `DiscordApiClientTests` (mocked `HttpMessageHandler`): success; 429 `retry_after` then success; 5xx retries
    then returns false; network exception never throws; non-429 4xx not retried; `allowed_mentions.parse` sent
    as `[]`; channel check reports reasons.
  - `DiscordNotifierTests`: first blood enqueued; Normal ignored; 2nd/3rd gated by config; disabled ignored;
    games filter; cheat mapped; cheat disabled ignored; queue-full drop without throwing.
- **Integration tests** (`GZCTF.Integration.Test`): **176 passed, 0 failed** — includes 3 new
  `DiscordNotificationHookTests`:
  - a legitimate first solve enqueues **exactly one** first-blood notification and no cheat;
  - a shared-flag submission enqueues **exactly one** cheat notification and **no** first blood;
  - with Discord disabled, scoring and submission results are unchanged.

Build: backend, unit and integration test projects all build with **0 errors**.

---

## 7. Limitations / follow-ups

- **REST only (no gateway).** The bot posts via the REST API and never opens a websocket, so it will not show
  as "online" in the member list. This is intentional (simpler, lighter) and does not affect posting.
- **Config is read once at startup.** Editing `discord.yml` requires a container restart to take effect.
- **At-least-queued, not guaranteed-delivered.** If Discord is down past the retry budget, or the bounded
  queue overflows during a burst, a message is logged and dropped rather than retried forever — by design, so
  flag checking is never affected.
- **Startup channel check is best-effort.** It verifies the bot can *see* the channel; a misconfigured
  *Send Messages*/*Embed Links* permission may still only surface as a failed send (logged) at post time.
- **No message templating/localization yet.** Embed text is English and fixed; could be made configurable or
  localized later.
- **Second/third blood** reuse the same hook and the same final tier as the in-game notice; they are only sent
  when `include_second_third_blood: true`.
