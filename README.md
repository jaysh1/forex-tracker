# Forex HITL Assistant (.NET 10 + Vue)

Cloud-native forex signal assistant with human-in-the-loop approval through a Vue mobile web page.
The .NET backend talks directly to OANDA v20 over HTTPS - no desktop terminal.
**Defaults to OANDA's practice (paper) environment.**

```
TechnicalAnalyticsAgent --signal--> ExecutionAgent --queues--> Pending list
   (5m/15m, EMA, RSI)                 |  ^ sizes + gates          |
                                      |  +-- GatekeeperAgent      v
                                      |      (spread, news)   Vue page (password login)
                                      +<--- POST /api/proposals/{id}/approve | reject
                                      v   re-validates, then POSTs the order to OANDA
```

One container serves both the API and the web page, so there is nothing else to host.

## Hard rules (see `Risk/RiskLimits.cs`)

| Rule | Value |
|---|---|
| Risk per trade | Lower of 0.5% of current NAV and `$50` in account currency; lot size rounded **down** |
| Daily drawdown cap | 1.5% of the lower of start-of-day NAV and the capital implied by the per-trade cap, then 24h lockout |
| Daily profit cap | 1.0% of that same base, then no trading until the next session (22:00 UTC rollover) |
| Reward:risk | exactly 1:2 (TP = 2 x SL distance) |
| Spread cap | 0.5 pips |
| News blackout | +/-30 min around High-impact events for either currency in the pair |
| Trading hours | Mon-Fri 09:00-17:00 Melbourne time by default (see below); no new proposals and no approvals outside them |
| Extra rails (additions) | max 10x leverage, one position per pair, 5 min proposal expiry, two-tap approve, fail-closed if calendar is down or saved risk state cannot be loaded |

All money figures (equity, risk, P&L) are in your **account's own currency**, read from OANDA. Pip values are USD,
so on a non-USD account (e.g. AUD) risk is converted at the live rate, rounded in the cautious direction. At approval
the order is re-sized against current equity and rate: never larger than you approved, and refused if it would have
to shrink by more than 5%. The page shows plain "$" for these amounts whatever the currency.

The default `$50` per-trade cap keeps a `$100,000` balance from increasing planned risk to `$500`. Set
`MAX_RISK_PER_TRADE` to a different positive amount in the OANDA account's currency if needed. The daily loss and
profit limits scale with the lower of start-of-day NAV and the capital implied by that cap (cap divided by 0.5%).

Daily P&L is measured on NAV, so unrealised losses count. The day's starting equity and any lockout are saved
(to Supabase, or to a local file - see below) so a restart cannot reset the baseline or clear a lockout.

## Trading hours

By default the bot only looks for signals, proposes trades and accepts approvals **Monday to Friday, 09:00 to
17:00 Melbourne time** (daylight saving is handled automatically). Outside those hours the page still shows your
equity and risk status, and Activity logs "Outside trading hours". A trade you opened earlier keeps its stop loss and
take profit at OANDA, which work around the clock. Reject always works. Change the window with
`TRADING_HOURS`, `TRADING_DAYS` and `TRADING_TIMEZONE`, or set `TRADING_HOURS=always`.

## Saving state to Supabase

The Forex and CFD **risk guard states** (each day's starting equity and any drawdown/profit lockout) and the **recent
activity feed** are saved so they survive restarts and redeploys. Pending proposals (they expire in 5 minutes) and
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
- Without `SUPABASE_URL`, Forex state goes to `GUARD_STATE_PATH` and CFD state goes to an account-specific file based
  on `CFD_GUARD_STATE_PATH` (or `GUARD_STATE_PATH` with a `.cfd` suffix). Local files only survive restarts on a
  persistent disk. With Supabase you do **not** need a disk, so a free Render instance no longer loses guard state.
- Rows are keyed by environment, account, and engine: Forex uses `practice:<account id>:guard`, while CFD uses `practice:<account id>:cfd_guard`. Switching accounts or engines cannot load another guard's baseline or lockout.
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
| `MAX_RISK_PER_TRADE` | Optional, default `50`; hard per-trade cap in the account's currency, in addition to the 0.5% NAV limit |
| `GUARD_STATE_PATH` | Optional, used only without Supabase. Default `data/guard-state.json` (the Docker image sets `/app/data/guard-state.json`) |
| `CFD_INSTRUMENTS` | Optional, empty by default; currently supports `AU200_AUD,SPX500_USD` |
| `CFD_MAX_RISK_PER_TRADE` | Optional, default `50`; caps CFD per-trade risk and scales its separate daily loss/profit limits |
| `CFD_MAX_SPREAD_POINTS_<INSTRUMENT>` | Required per enabled CFD; positive cap in the instrument's quoted price points, checked during scan and approval |
| `CFD_MAX_APPROVAL_DRIFT_POINTS_<INSTRUMENT>` | Required per enabled CFD; positive maximum price movement in quoted points between proposal and approval |
| `CFD_GUARD_STATE_PATH` | Optional base path for the local CFD guard file; environment and account are appended for isolation |

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

## Develop locally

The Vue dashboard uses Vite hot module replacement. Keep the API and UI running in separate terminals; UI edits appear in the browser without rebuilding the .NET app or restarting the API.

Terminal 1 (the usual local environment variables can also come from `launchSettings.json`):

```bash
dotnet run --project src/ForexHitl --urls http://localhost:5000
```

Terminal 2:

```bash
cd src/ForexHitl.Web
npm ci
npm run dev
```

Open <http://localhost:5173>. Vite proxies `/api` requests to the .NET server on port 5000. Run `npm run build` when you want to generate the production client files; the Docker build does this automatically.

Run the backend tests with `dotnet test` from the repository root.

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
- The Vue dashboard is presentation-only. Its TypeScript API types (`src/ForexHitl.Web/src/types.ts`) mirror the server display contracts in `src/ForexHitl/DashboardPresentation.cs`; keep them in sync. Trading calculations, decisions, and formatted display values stay on the backend.
