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

Daily P&L is measured on NAV, so unrealised losses count. Guard state persists to `GUARD_STATE_PATH`
so a restart cannot reset the baseline or clear a lockout - **mount a persistent disk there in production**.

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
