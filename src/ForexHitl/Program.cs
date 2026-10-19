using ForexHitl;
using Microsoft.AspNetCore.StaticFiles;

var config = AppConfig.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DailyRiskGuard(config.GuardStatePath));
builder.Services.AddSingleton<PendingTradeStore>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<SessionAuth>();

builder.Services.AddHttpClient<IBrokerClient, OandaClient>(c =>
{
    c.BaseAddress = new Uri(config.OandaBaseUrl);
    c.DefaultRequestHeaders.Authorization = new("Bearer", config.OandaToken);
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<INewsCalendar, HttpNewsCalendar>(c => c.Timeout = TimeSpan.FromSeconds(15));

if (config.NtfyTopic is null)
    builder.Services.AddSingleton<IApprovalNotifier, NullNotifier>();
else
    builder.Services.AddHttpClient<IApprovalNotifier, NtfyNotifier>(c => c.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddSingleton<GatekeeperAgent>();
builder.Services.AddSingleton<ExecutionAgent>();
builder.Services.AddHostedService<TechnicalAnalyticsAgent>();

var app = builder.Build();

// Basic hardening. X-Frame-Options matters most: nobody should be able to frame this page and trick a tap
// onto the Approve button.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    await next();
});

// --- Blazor WebAssembly client (published into wwwroot by the Dockerfile) -------------------------------
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true, // Blazor ships .dat/.blat/.dll/.wasm files
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" // always revalidate after a deploy
});

// Never let the browser cache API responses.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

// Public, no login, no secrets: for uptime monitors / cron pings and Render's health check. GET and HEAD.
app.MapMethods("/health", new[] { "GET", "HEAD" }, () => Results.Text("OK"));

// --- Approval API ---------------------------------------------------------------------------------------
app.MapPost("/api/login", async (LoginRequest req, SessionAuth auth) =>
{
    var result = auth.Login(req.Password);
    if (result.Ok) return Results.Ok(new LoginResponse(result.Token!));

    await Task.Delay(500); // slow down guessing
    return Results.Json(new { error = result.Error }, statusCode: result.Locked ? 429 : 401);
});

var secured = app.MapGroup("/api");
secured.AddEndpointFilter(async (ctx, next) =>
{
    var auth = ctx.HttpContext.RequestServices.GetRequiredService<SessionAuth>();
    var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !auth.IsValid(header[7..]))
        return Results.Unauthorized();
    return await next(ctx);
});

secured.MapPost("/logout", (HttpRequest request, SessionAuth auth) =>
{
    auth.Logout(request.Headers.Authorization.ToString()[7..]);
    return Results.NoContent();
});

secured.MapGet("/state", (
    ExecutionAgent execution, PendingTradeStore store, ActivityLog activity, AppConfig cfg, TimeProvider time) =>
{
    var s = execution.LastStatus;
    var status = new StatusDto(
        cfg.UseLive ? "live" : "practice", s is not null, s?.UpdatedUtc, s?.Equity, s?.DayPnl,
        s?.CanTrade ?? true, s?.Reason, time.GetUtcNow().UtcDateTime);

    var pending = store.Snapshot().Select(p => new PendingDto(
        p.Id, p.Instrument, p.Side.ToString(), p.Units, p.EntryPrice, p.StopLoss, p.TakeProfit,
        p.StopLossPips, p.RiskUsd, p.CreatedUtc, p.CreatedUtc + ExecutionAgent.ProposalTtl, p.Rationale)).ToList();

    var feed = activity.Snapshot().Select(a => new ActivityDto(a.TimeUtc, a.Kind, a.Text)).ToList();
    return Results.Ok(new StateDto(status, pending, feed));
});

secured.MapPost("/proposals/{id}/{action}", async (string id, string action, ExecutionAgent execution, ILogger<Program> log) =>
{
    if (action is not ("approve" or "reject")) return Results.BadRequest();
    try
    {
        // CancellationToken.None on purpose: if the phone drops the connection we must not abort a
        // half-sent order. HttpClient timeouts bound the work.
        return Results.Ok(await execution.HandleDecisionAsync(action, id, CancellationToken.None));
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed handling decision {Action} {Id}", action, id);
        return Results.Ok(new DecisionResult(false, "Error - check the logs and your OANDA account."));
    }
});

app.MapFallback("/api/{**rest}", () => Results.NotFound());
if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
    app.MapFallbackToFile("index.html");
else
    app.MapFallback(() => Results.Content(
        "<!doctype html><meta charset=utf-8><title>Forex approver</title>" +
        "<body style=\"font-family:sans-serif;max-width:40rem;margin:2rem auto;padding:0 1rem\">" +
        "<h1>Server is running, web page not built yet</h1>" +
        "<p>Publish the Blazor client into this server's <code>wwwroot</code> folder, then restart:</p>" +
        "<pre>dotnet publish src/ForexHitl.Client -c Release -o client-out\n" +
        "(copy the contents of client-out/wwwroot into src/ForexHitl/wwwroot)</pre>" +
        "<p>The API is up: <a href=\"/health\">/health</a></p></body>", "text/html"));

app.Run();
