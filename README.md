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
| Trading hours | Mon-Fri 09:00-17:00 Melbourne time by default (see below); no new proposals and no approvals outside them |
| Extra rails (additions) | max 10x leverage, one position per pair, 5 min proposal expiry, two-tap approve, fail-closed if calendar is down or saved risk state cannot be loaded |

All money figures (equity, risk, P&L) are in your **account's own currency**, read from OANDA. Pip values are USD,
so on a non-USD account (e.g. AUD) risk is converted at the live rate, rounded in the cautious direction. At approval
the order is re-sized against current equity and rate: never larger than you approved, and refused if it would have
to shrink by more than 5%. The page shows plain "$" for these amounts whatever the currency.

Daily P&L is measured on NAV, so unrealised losses count. The day's starting equity and any lockout are saved
(to Supabase, or to a local file - see below) so a restart cannot reset the baseline or clear a lockout.

## Trading hours

By default the bot only looks for signals, proposes trades and accepts approvals **Monday to Friday, 09:00 to
17:00 Melbourne time** (daylight saving is handled automatically). Outside those hours the page still shows your
equity and risk status, and Activity logs "Outside trading hours". A trade you opened earlier keeps its stop loss and
take profit at OANDA, which work around the clock. Reject always works. Change the window with
`TRADING_HOURS`, `TRADING_DAYS` and `TRADING_TIMEZONE`, or set `TRADING_HOURS=always`.

## Saving state to Supabase

Two things are saved so they survive restarts and redeploys: the **risk guard state** (the day's starting equity and
any drawdown/profit lockout) and the **recent activity feed**. Pending proposals (they expire in 5 minutes) and
sign-in sessions (a restart signs you out) deliberately stay in memory.

1. Create a project at supabase.com. In the dashboard open **SQL Editor** and run `supabase/schema.sql` (also below).
2. In **Project Settings > API Keys** copy the **secret** key (`sb_secret_...`) and your project URL
   (`https://<ref>.supabase.co`). Never use the publishable/anon key, and never put the secret key in the repo or the browser.
3. Set `SUPABASE_URL` and `SUPABASE_SECRET_KEY` as environment variables (Render: Environment tab), then restart.

```sql
create table if not exists public.bot_state (
  key        text primary key,
  value      jsonb not null,
  updated_at timestamptz not null default now()
);
alter table public.bot_state enable row level security;   -- no policies: only the secret key can use the table
revoke all on public.bot_state from anon, authenticated;
grant select, insert, update on public.bot_state to service_role;
```

How it behaves:

- **Fails safe on load.** If the saved risk state cannot be read (wrong key, Supabase down, corrupt row), trading stays
  **paused** and Activity says why, retrying every 20 seconds. Carrying on with a fresh baseline could silently
  erase a lockout, so it does not.
- **Keeps going on save failure.** If a save fails, trading continues on the in-memory state and the save is retried
  on every tick; Activity reports it (a restart during that time could lose the newest change).
- The risk state is saved the moment it changes; the activity feed is saved every 10 seconds, and both once more on shutdown.
- Without `SUPABASE_URL`, state goes to a local file (`GUARD_STATE_PATH`), which only survives restarts on a
  persistent disk. With Supabase you do **not** need a disk, so a free Render instance no longer loses the daily-loss state.
- Rows are keyed by environment and account (`practice:<account id>:guard`), so switching between practice and live, or between accounts, never loads another one's baseline or lockout.
- A saved risk state that does not make sense (zero baseline, bad session time, a lock with no reason) is treated like an unreadable one: trading stays paused.
- For local development against the Supabase CLI's stack, `http://127.0.0.1:54321` is accepted; every other URL must be https.

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
| `SUPABASE_URL`, `SUPABASE_SECRET_KEY` | Optional, set both or neither. Project URL and **secret** key; saves risk state and activity to Supabase |
| `TRADING_HOURS` | Optional, default `09:00-17:00` (`HH:mm-HH:mm`, cannot cross midnight), or `always` |
| `TRADING_DAYS` | Optional, default `Mon-Fri` (also `Mon,Wed,Fri` or `Fri-Mon`) |
| `TRADING_TIMEZONE` | Optional, default `Australia/Melbourne` (IANA name) |
| `GUARD_STATE_PATH` | Optional, used only without Supabase. Default `data/guard-state.json` (the Docker image sets `/app/data/guard-state.json`) |

Never commit secrets. Use Codespaces secrets while developing and Render's environment settings in production.

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

The blueprint uses a paid instance with a 1 GB disk (free instances sleep after 15 minutes and have no
persistent disk). Keep it to **one instance**: pending approvals and the guard live in memory.

## Known limitations - read before trusting it

- **The strategy is a textbook starting point, not a proven edge.** Run it on practice for weeks and review results before anything else.
- **0.5 pip spread cap** is very tight; OANDA's standard pricing on EUR/USD is usually wider, so the gatekeeper may block most signals. The Activity list shows every skipped signal and why. Adjust `RiskLimits.MaxSpreadPips` deliberately if so.
- The free economic-calendar feed is unofficial. The gatekeeper fails closed when it is unavailable.
- If an order request fails in flight, the outcome is unknown; the page tells you to check OANDA rather than retrying.
- Only USD-quoted majors (EUR_USD, GBP_USD) are supported; other pairs need pip-value conversion.
- Sessions and the activity feed are in memory: a restart signs you out and clears the feed (guard state is kept).
- The Blazor client DTOs (`src/ForexHitl.Client/Dtos.cs`) are a copy of the server's (`Approval/ApiModels.cs`); keep them in sync.
