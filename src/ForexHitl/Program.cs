using ForexHitl;
using CFDEngine;
using Microsoft.AspNetCore.StaticFiles;

var config = AppConfig.FromEnvironment();
var cfdSettings = CfdSettings.FromEnvironment(config.UseLive);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(cfdSettings);
builder.Services.AddSingleton(sp => new CfdDailyRiskGuard(
    maxRiskPerTrade: cfdSettings.MaxRiskPerTrade, awaitingRestore: true));
var cfdAccountScope = $"{(config.UseLive ? "live" : "practice")}_{config.OandaAccountId.Replace('-', '_')}";
if (config.SupabaseUrl is not null)
    // The configured IStateStore is already scoped by environment and account.
    builder.Services.AddSingleton<ICfdRiskStateStore>(sp => new CfdRiskStateStoreAdapter(
        sp.GetRequiredService<IStateStore>(), "cfd_guard"));
else
{
    var cfdGuardPath = Environment.GetEnvironmentVariable("CFD_GUARD_STATE_PATH") ?? config.GuardStatePath + ".cfd";
    builder.Services.AddSingleton<ICfdRiskStateStore>(new CfdFileRiskStateStore($"{cfdGuardPath}.{cfdAccountScope}"));
}
builder.Services.AddSingleton(sp => new CfdRiskPersistence(
    sp.GetRequiredService<CfdDailyRiskGuard>(), sp.GetRequiredService<ICfdRiskStateStore>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CfdRiskPersistence>>(),
    message => sp.GetRequiredService<ActivityLog>().Add("error", message)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(config.TradingHours);
// The guard refuses to allow trading until its saved state has been loaded (see GuardPersistence).
builder.Services.AddSingleton(new DailyRiskGuard(
    awaitingRestore: true, maxRiskPerTrade: config.MaxRiskPerTrade));
builder.Services.AddSingleton<GuardPersistence>();
builder.Services.AddSingleton<PendingTradeStore>();
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<ScanStatusStore>();
builder.Services.AddSingleton<DashboardPresenter>();
builder.Services.AddSingleton<DecisionConfirmationStore>();
builder.Services.AddSingleton<SessionAuth>();

// State store: Supabase when configured, otherwise a local file (only durable on a persistent disk).
if (config.SupabaseUrl is not null)
{
    builder.Services.AddHttpClient<SupabaseStateStore>(c =>
        SupabaseStateStore.Configure(c, config.SupabaseUrl, config.SupabaseSecretKey!));
    var scope = $"{(config.UseLive ? "live" : "practice")}:{config.OandaAccountId}:";
    builder.Services.AddSingleton<IStateStore>(sp =>
        new ScopedStateStore(sp.GetRequiredService<SupabaseStateStore>(), scope));
}
else
    builder.Services.AddSingleton<IStateStore>(new FileStateStore(config.GuardStatePath));

builder.Services.AddHttpClient<IBrokerClient, OandaClient>(c =>
{
    c.BaseAddress = new Uri(config.OandaBaseUrl);
    c.DefaultRequestHeaders.Authorization = new("Bearer", config.OandaToken);
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("cfd", c =>
{
    // CFD practice has its own client pinned to OANDA's practice endpoint. It cannot inherit live settings.
    c.BaseAddress = new Uri("https://api-fxpractice.oanda.com/");
    c.DefaultRequestHeaders.Authorization = new("Bearer", config.OandaToken);
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddTransient(sp => new CfdOandaClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("cfd"), config.OandaAccountId));
builder.Services.AddTransient<ICfdBroker>(sp => sp.GetRequiredService<CfdOandaClient>());
builder.Services.AddHttpClient<INewsCalendar, HttpNewsCalendar>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<ICfdNewsCalendar, CfdNewsCalendarAdapter>();

if (config.NtfyTopic is null)
    builder.Services.AddSingleton<IApprovalNotifier, NullNotifier>();
else
    builder.Services.AddHttpClient<IApprovalNotifier, NtfyNotifier>(c => c.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddSingleton<GatekeeperAgent>();
builder.Services.AddSingleton<ICfdStrategy, CfdTrendPullbackStrategy>();
builder.Services.AddSingleton<CfdPracticeAgent>();
builder.Services.AddSingleton(sp => new ExecutionAgent(
    sp.GetRequiredService<IBrokerClient>(),
    sp.GetRequiredService<GatekeeperAgent>(),
    sp.GetRequiredService<DailyRiskGuard>(),
    sp.GetRequiredService<PendingTradeStore>(),
    sp.GetRequiredService<ActivityLog>(),
    sp.GetRequiredService<IApprovalNotifier>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ExecutionAgent>>(),
    sp.GetRequiredService<GuardPersistence>(),
    sp.GetRequiredService<TradingWindow>(),
    config.MaxRiskPerTrade));
// Order matters: hosted services start in registration order, so saved state is loaded before scanning starts.
builder.Services.AddHostedService<StateSyncService>();
builder.Services.AddHostedService<TechnicalAnalyticsAgent>();
if (cfdSettings.Enabled) builder.Services.AddHostedService(sp => sp.GetRequiredService<CfdPracticeAgent>());

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

// --- Vue client (built into wwwroot by the Dockerfile) -------------------------------------------------
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
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

secured.MapGet("/cfd/state", (
    CfdPracticeAgent cfd, DashboardPresenter presenter, DecisionConfirmationStore confirmations, TimeProvider time) =>
    Results.Ok(presenter.Cfd(cfd.Snapshot(), confirmations, time.GetUtcNow().UtcDateTime)));
secured.MapPost("/cfd/proposals/{id}/{action}", async (
    string id, string action, HttpRequest request, CfdPracticeAgent cfd, DashboardPresenter presenter,
    DecisionConfirmationStore confirmations, TimeProvider time, ILogger<Program> log) =>
{
    if (action is not ("approve" or "reject")) return Results.BadRequest();
    if (action == "approve" && !confirmations.TryConfirm("cfd", id, request.Headers["X-Approval-Request-Id"].ToString(), time.GetUtcNow().UtcDateTime))
        return Results.Ok(new DashboardActionResult(true, false, true, "Tap approve again within 20 seconds to confirm."));
    if (action == "reject") confirmations.Clear("cfd", id);
    try { return Results.Ok(presenter.CfdAction(await cfd.DecideAsync(action, id, CancellationToken.None), action)); }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed handling CFD practice decision {Action} {Id}", action, id);
        return Results.Ok(new DashboardActionResult(false, false, false,
            "CFD order failed - check your OANDA practice account before retrying."));
    }
});

secured.MapGet("/state", (
    ExecutionAgent execution, PendingTradeStore store, ActivityLog activity, ScanStatusStore scanStatus,
    DecisionConfirmationStore confirmations,
    AppConfig cfg, TradingWindow hours, DashboardPresenter presenter, TimeProvider time) =>
{
    var s = execution.LastStatus;
    var now = time.GetUtcNow().UtcDateTime;
    return Results.Ok(presenter.Forex(cfg.UseLive, s, hours, now,
        scanStatus.Snapshot(), store.Snapshot(), activity.Snapshot(), confirmations));
});

secured.MapPost("/proposals/{id}/{action}", async (
    string id, string action, HttpRequest request, ExecutionAgent execution, DashboardPresenter presenter,
    DecisionConfirmationStore confirmations, TimeProvider time, ILogger<Program> log) =>
{
    if (action is not ("approve" or "reject")) return Results.BadRequest();
    if (action == "approve" && !confirmations.TryConfirm("forex", id, request.Headers["X-Approval-Request-Id"].ToString(), time.GetUtcNow().UtcDateTime))
        return Results.Ok(new DashboardActionResult(true, false, true, "Tap approve again within 20 seconds to confirm."));
    if (action == "reject") confirmations.Clear("forex", id);
    try
    {
        // CancellationToken.None on purpose: if the phone drops the connection we must not abort a
        // half-sent order. HttpClient timeouts bound the work.
        return Results.Ok(presenter.Action(await execution.HandleDecisionAsync(action, id, CancellationToken.None), action));
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed handling decision {Action} {Id}", action, id);
        return Results.Ok(new DashboardActionResult(false, false, false,
            "Error - check the logs and your OANDA account."));
    }
});

app.MapFallback("/api/{**rest}", () => Results.NotFound());
if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
    app.MapFallbackToFile("index.html");
else
    app.MapFallback(() => Results.Content(
        "<!doctype html><meta charset=utf-8><title>Forex approver</title>" +
        "<body style=\"font-family:sans-serif;max-width:40rem;margin:2rem auto;padding:0 1rem\">" +
        "<h1>API server is running</h1>" +
        "<p>Start the Vue development server from <code>src/ForexHitl.Web</code> to open the dashboard.</p>" +
        "<p>The API is up: <a href=\"/health\">/health</a></p></body>", "text/html"));

app.Run();
