# Forex HITL Assistant (.NET 8)

Cloud-native forex signal assistant with human-in-the-loop approval through a mobile web page
(Blazor WebAssembly). Talks directly to OANDA v20 over HTTPS - no desktop terminal.
**Defaults to OANDA's practice (paper) environment.**

```
TechnicalAnalyticsAgent --signal--> ExecutionAgent --queues--> Pending list
   (5m/15m, EMA, RSI)                 |  ^ sizes + gates          |
                                      |  +-- GatekeeperAgent      v
                                      |      (spread, news)   Blazor WASM page (password login)
                                      +<--- POST /api/proposals/{id}/approve | reject
                                      v   re-validates, then POSTs the order to OANDA
```

One container serves both the API and the web page, so there is nothing else to host.

## Hard rules (see `Risk/RiskLimits.cs`)

| Rule | Value |
|---|---|
| Risk per trade | 0.5% of current NAV (`$50` on `$10k`), lot size rounded **down** |
| Daily drawdown cap | 1.5% of start-of-day NAV, then 24h lockout |
| Daily profit cap | 1.0%, then no trading until the next session (22:00 UTC rollover) |
| Reward:risk | exactly 1:2 (TP = 2 x SL distance) |
| Spread cap | 0.5 pips |
| News blackout | +/-30 min around High-impact events for either currency in the pair |
| Extra rails (additions) | max 10x leverage, one position per pair, 5 min proposal expiry, two-tap approve, fail-closed if calendar is down |

All money figures (equity, risk, P&L) are in your **account's own currency**, read from OANDA. Pip values are USD,
so on a non-USD account (e.g. AUD) risk is converted at the live rate, rounded in the cautious direction. At approval
the order is re-sized against current equity and rate: never larger than you approved, and refused if it would have
to shrink by more than 5%. The page shows plain "$" for these amounts whatever the currency.

Daily P&L is measured on NAV, so unrealised losses count. Guard state is saved to Supabase when configured;
otherwise it uses `GUARD_STATE_PATH` (which needs a persistent disk to survive redeploys).
Risk state must load and save successfully before a new proposal or approved order can proceed.

## Trading hours

New proposals and approved orders are allowed **09:00 inclusive to 17:00 exclusive, Monday–Friday,
Australia/Melbourne** by default. Melbourne daylight saving is automatic. The API checks the window on
every approval, including again after network checks, so a proposal at 16:59 cannot execute at 17:00.
The page shows the configured hours and a paused reason when outside them. The scanner skips candle
analysis outside the window; while the server is awake it still refreshes account/risk status.

This controls new entries, not existing positions: it does **not** close trades at 17:00. Their broker-side
stop loss and take profit remain active even if Render sleeps. It also does not stop or start Render itself.

## Environment variables

| Variable | Notes |
|---|---|
| `OANDA_API_TOKEN`, `OANDA_ACCOUNT_ID` | From your OANDA **practice** account |
| `OANDA_ENVIRONMENT` | `practice` (default) or `live`. `live` additionally requires `ALLOW_LIVE_TRADING=true` |
| `APPROVER_PASSWORD` | 12+ characters. Signs you in to the approval page. Use a unique one |
| `NTFY_TOPIC` | Optional. 16-64 chars of `A-Za-z0-9_-`, long and random (it is the only secret on that feed). Enables push via ntfy |
| `NTFY_SERVER` | Optional, default `https://ntfy.sh` |
| `PUBLIC_BASE_URL` | Optional. Public URL of the app; a tap on the ntfy notification opens it |
| `INSTRUMENTS` | Optional, default `EUR_USD,GBP_USD` |
| `CALENDAR_FEED_URLS` | Optional, comma-separated JSON feeds (default: Faireconomy weekly export) |
| `GUARD_STATE_PATH` | Optional, default `data/guard-state.json` (the Docker image sets `/app/data/guard-state.json`) |
| `TRADING_TIME_ZONE` | Optional, default `Australia/Melbourne` (IANA zone, daylight saving applied) |
| `TRADING_START`, `TRADING_END` | Optional, defaults `09:00`, `17:00`; `HH:mm`, same day, end excluded |
| `TRADING_DAYS` | Optional, default `Monday,Tuesday,Wednesday,Thursday,Friday` |
| `SUPABASE_URL` | Optional HTTPS project URL, such as `https://your-project.supabase.co`; set with a server key |
| `SUPABASE_SECRET_KEY` | Server-only `sb_secret_...` key; never put it in Blazor or commit it |
| `SUPABASE_SERVICE_ROLE_KEY` | Legacy service-role JWT alternative to `SUPABASE_SECRET_KEY` |

Never commit secrets. Use Codespaces secrets while developing and Render's environment settings in production.

## Supabase state on a free Render instance

1. In your Supabase project's SQL editor, run [`supabase/forex_state.sql`](supabase/forex_state.sql).
   This creates the `forex_state` table, enables RLS and denies access to browser/anonymous users.
2. In Render's server environment, set `SUPABASE_URL` and `SUPABASE_SECRET_KEY`.
   Use the project's server secret key, or set the legacy `SUPABASE_SERVICE_ROLE_KEY` instead.
   A publishable/anon key is deliberately rejected. Do not paste server keys into chat or the client.
3. Redeploy. The server restores its state automatically from rows scoped to OANDA account and
   environment, so practice and live state cannot mix. Use **one running server per account/environment**;
   this is not a distributed multi-instance execution system. Avoid overlapping deployments against the same account.

The daily baseline and lockout are saved synchronously before each guard evaluation returns (roughly
every 20 seconds while awake, plus proposal/approval checks). Errors, missing tables and invalid stored
guard state pause entries and retry; they never fall back to a new local baseline. An empty table on the
first run creates a baseline from current NAV. Existing local state is **not** automatically migrated:
enable Supabase at a fresh forex session with no active lockout, or migrate the existing guard record first.

The latest 50 activity entries are restored on restart and changes are saved about every 20 seconds,
with a best-effort flush on graceful shutdown. A hard crash can lose the last few activity messages.
Sessions and pending approvals are not restored: sign in again, and wait for fresh proposals after a
restart. Account equity is fetched fresh from OANDA, not restored from a stale account snapshot.

For your external uptime cron, select **Australia/Melbourne** as the scheduler's timezone and use:

```cron
*/10 9-16 * * 1-5
```

That pings every 10 minutes from 09:00 through 16:50, Monday–Friday. You can also add a separate
17:00 ping (`0 17 * * 1-5`) to keep the page awake through closing time. A ping wakes a sleeping free
service and can take time; a cron ping is not a guarantee of continuous uptime. The application enforces
trading hours independently of these pings. This cron belongs in your existing uptime service, not in GitHub Actions.

## Push notifications (optional)

A web page cannot buzz your phone when it is closed. Install the free **ntfy** app, subscribe to your
`NTFY_TOPIC`, and you get a notification when a trade needs approval or trading is paused. Tapping it opens the page.
Without ntfy, keep the page open or check it yourself - proposals expire after 5 minutes.

## First run

Open the service URL and sign in with `APPROVER_PASSWORD`. The Account card fills in within about 20 seconds.

- "Waiting for the first update from OANDA" for more than a minute: the token or account ID is wrong, or OANDA is
  unreachable. The reason appears in **Activity** as "Scanner problem: ...".
- Yellow "Scanner has not reported for N min": the scanner has stopped updating. Check the server logs.
- The Activity list also records every signal the bot skipped and why (spread, news, lockout, sizing).

## Health check

`GET` or `HEAD /health` returns `200 OK` with the text `OK`. No login, no secrets. Point a cron job, uptime monitor or
Render's health check at it. It only proves the web server is up, not that the scanner is working; for that, look at
the Account card on the page (a yellow banner appears if the scanner stops reporting).

## Develop in GitHub Codespaces

```bash
dotnet test
# server on :5000 (client must be published into the server's wwwroot to see the UI):
dotnet publish src/ForexHitl.Client -c Release -o /tmp/client
mkdir -p src/ForexHitl/wwwroot && cp -r /tmp/client/wwwroot/. src/ForexHitl/wwwroot/
dotnet run --project src/ForexHitl --urls http://0.0.0.0:5000
```

## Deploy to Render

1. Push this repo to GitHub (private).
2. Render: **New > Blueprint**, pick the repo. It reads `render.yaml` and asks for the secret values.
3. Open the service URL on your phone and sign in with `APPROVER_PASSWORD`.

The blueprint uses a paid instance with a 1 GB disk. Your manually-created free service can use Supabase
for durable guard state without a disk; set its environment variables manually. External state does not
prevent free-service sleep, outages or platform usage limits. Keep it to **one instance** per account/environment.

## Known limitations - read before trusting it

- **The strategy is a textbook starting point, not a proven edge.** Run it on practice for weeks and review results before anything else.
- **0.5 pip spread cap** is very tight; OANDA's standard pricing on EUR/USD is usually wider, so the gatekeeper may block most signals. The Activity list shows every skipped signal and why. Adjust `RiskLimits.MaxSpreadPips` deliberately if so.
- The free economic-calendar feed is unofficial. The gatekeeper fails closed when it is unavailable.
- If an order request fails in flight, the outcome is unknown; the page tells you to check OANDA rather than retrying.
- Only USD-quoted majors (EUR_USD, GBP_USD) are supported; other pairs need pip-value conversion.
- Sessions and pending approvals are in memory: a restart signs you out and discards pending proposals.
  Guard state and the recent activity feed survive when Supabase or a persistent local disk is configured.
- The Blazor client DTOs (`src/ForexHitl.Client/Dtos.cs`) are a copy of the server's (`Approval/ApiModels.cs`); keep them in sync.
