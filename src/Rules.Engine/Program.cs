// NOTE to maintainers:
// This file scaffolds a minimal Rules Engine so teams can plug in rule evaluation
// without first wiring all the plumbing. The plumbing (config, DbContexts, SignalR
// client, background worker loop) is in place – the actual rule logic is NOT.
// Packages you likely need in the .csproj (add via PackageReference):
//   - Microsoft.EntityFrameworkCore
//   - Npgsql.EntityFrameworkCore.PostgreSQL
//   - Microsoft.AspNetCore.SignalR.Client
//
// Connection strings are read from appsettings.json or environment variables:
//   - ConnectionStrings:RulesDb  (default: Host=localhost;Username=postgres;Password=password;Database=innovia_rules)
//   - ConnectionStrings:IngestDb (default: Host=localhost;Username=postgres;Password=password;Database=innovia_ingest)
// Hub URL:
//   - Realtime:HubUrl (default: http://localhost:5103/hub/telemetry)
//
// TODOs are intentionally terse and technical; treat them as guidance...

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Trim noisy EF Core SQL logs
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.Warning);

// --- Config (with sensible defaults for local dev) ---
var rulesConn = builder.Configuration.GetConnectionString("RulesDb")
    ?? "Host=localhost;Port=5434;Username=postgres;Password=password;Database=innovia_rules";
var ingestConn = builder.Configuration.GetConnectionString("IngestDb")
    ?? "Host=localhost;Port=5434;Username=postgres;Password=password;Database=innovia_ingest";
var hubUrl = builder.Configuration.GetSection("Realtime")["HubUrl"]
    ?? "http://localhost:5103/hub/telemetry";

// --- Services ---
builder.Services.AddDbContext<RulesDbContext>(o => o.UseNpgsql(rulesConn));
builder.Services.AddDbContext<IngestReadDbContext>(o => o.UseNpgsql(ingestConn));

// SignalR client to publish alerts in real time
builder.Services.AddSingleton<HubConnection>(_ =>
    new HubConnectionBuilder()
        .WithUrl(hubUrl)
        .WithAutomaticReconnect()
        .Build());

// Background worker
builder.Services.AddHostedService<RulesWorker>();

var app = builder.Build();

// Ensure DB exists (MVP convenience). In production, prefer EF migrations.
using (var scope = app.Services.CreateScope())
{
    var rulesDb = scope.ServiceProvider.GetRequiredService<RulesDbContext>();
    rulesDb.Database.EnsureCreated();
}

// --- Endpoints ---
// TODO: POST /rules – create a device-scoped rule. Suggested body:
//   { "tenantId":"...", "deviceId":"...", "type":"temperature", "op":">", "threshold": 28.0 }
app.MapPost("/rules", async (RulesDbContext db, IngestReadDbContext ingest, RuleRow row) =>
{
    row.Id = Guid.NewGuid();
    
    row.TenantId = await ingest.Measurements
        .Where(m => m.DeviceId == row.DeviceId)
        .Select(m => m.TenantId)
        .FirstOrDefaultAsync();
    
    db.Rules.Add(row);
    await db.SaveChangesAsync();

    return Results.Created($"/rules/{row.Id}", row);
});

// TODO: GET /rules – list rules.
app.MapGet("/rules", async (RulesDbContext db) =>
{
    var rules = await db.Rules.ToListAsync();
    
    return Results.Ok(rules);
});

// TODO: GET /alerts – list latest alerts (filter on tenant/device is nice to have).
app.MapGet("/alerts", async (RulesDbContext db) =>
{
    var alerts = await db.Alerts
        .OrderByDescending(a => a.Time)
        .Take(10)
        .ToListAsync();
    
    return Results.Ok(alerts);
});

app.MapGet("/", () => Results.Ok(new { service = "Rules.Engine", status = "ok" }));

// Start SignalR connection on boot
using (var scope = app.Services.CreateScope())
{
    var conn = scope.ServiceProvider.GetRequiredService<HubConnection>();
    try
    {
        await conn.StartAsync();
        app.Logger.LogInformation("Connected to Realtime Hub at {HubUrl}", hubUrl);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to connect to Realtime Hub at {HubUrl}.", hubUrl);
    }
}

app.Run();


// ===========================
// Background worker
// ===========================
public class RulesWorker : BackgroundService
{
    private readonly ILogger<RulesWorker> _log;
    private readonly RulesDbContext _rules;
    private readonly IngestReadDbContext _ingest;
    private readonly HubConnection _hub;

    public RulesWorker(ILogger<RulesWorker> log, RulesDbContext rules, IngestReadDbContext ingest, HubConnection hub)
    {
        _log = log;
        _rules = rules;
        _ingest = ingest;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Rules engine started (poll=10s)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // TODO: Load enabled rules from _rules.
                // TODO: For each rule, fetch the latest measurement (device + type) from _ingest.
                // TODO: Compare value against threshold using the rule's operator.
                // TODO: Avoid alert spam (cooldown per rule/device?).
                // TODO: Store an AlertRow and push it to Realtime.Hub.
                //       NOTE: Realtime.Hub has no server method for alerts yet – it must be added there.
                //       Browser clients should receive it as "alertRaised" in the tenant's group.
                //       Hub groups are keyed on tenant *slug*, rules/measurements only know tenant *GUID*.
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled error during rule evaluation cycle.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}


// ===========================
// EF Core data model
// ===========================
public class RulesDbContext : DbContext
{
    public RulesDbContext(DbContextOptions<RulesDbContext> options) : base(options) { }
    public DbSet<RuleRow> Rules => Set<RuleRow>();
    public DbSet<AlertRow> Alerts => Set<AlertRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RuleRow>().ToTable("Rules").HasKey(r => r.Id);
        modelBuilder.Entity<AlertRow>().ToTable("Alerts").HasKey(a => a.Id);
        base.OnModelCreating(modelBuilder);
    }
}

public class IngestReadDbContext : DbContext
{
    public IngestReadDbContext(DbContextOptions<IngestReadDbContext> options) : base(options) { }
    public DbSet<MeasurementRow> Measurements => Set<MeasurementRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MeasurementRow>().ToTable("Measurements").HasKey(m => m.Id);
        base.OnModelCreating(modelBuilder);
    }
}

// --- Tables ---
public class RuleRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Type { get; set; } = ""; // e.g., "temperature"
    
    // TODO: operator, threshold, enabled, ...
    public string Operator { get; set; } = "";
    public double Threshold { get; set; }
    public bool Enabled { get; set; } = true;
    public string TenantSlug { get; set; } = "";
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AlertRow
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset Time { get; set; } =  DateTimeOffset.UtcNow;
    
    // TODO: value, severity, message, ...
    public double Value { get; set; }
    public string Message { get; set; } = "";
}

// Mirror of Ingest measurement (read-only model)
public class MeasurementRow
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = "";
    public double Value { get; set; }
}
