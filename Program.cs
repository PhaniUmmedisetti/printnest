using Microsoft.EntityFrameworkCore;
using PrintNest.Api.Middleware;
using PrintNest.Application.Commands;
using PrintNest.Application.Interfaces;
using PrintNest.Infrastructure.Auth;
using PrintNest.Infrastructure.Persistence;
using PrintNest.Infrastructure.Storage;
using PrintNest.Infrastructure.Workers;

// ─────────────────────────────────────────────────────────────────────────────
// PrintNest API — Program.cs
//
// Architecture: single project, layered by folder (Domain / Application /
//               Infrastructure / Api). No separate .csproj per layer.
//
// To add a new feature:
//   1. Add entity/enum in Domain/
//   2. Add interface in Application/Interfaces/
//   3. Add command in Application/Commands/
//   4. Add implementation in Infrastructure/
//   5. Register below
//   6. Add controller action in Api/Controllers/
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres"),
        npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3)
    )
);

// ── Infrastructure services ───────────────────────────────────────────────────
builder.Services.AddScoped<IStorageService, MinioStorageService>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IDeviceAuthService, HmacDeviceAuthService>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddScoped<IAuditService, AuditService>();

// ── Application commands ──────────────────────────────────────────────────────
builder.Services.AddScoped<CreateJobCommand>();
builder.Services.AddScoped<FinalizeUploadCommand>();
builder.Services.AddScoped<QuoteJobCommand>();
builder.Services.AddScoped<PayJobCommand>();
builder.Services.AddScoped<GenerateOtpCommand>();
builder.Services.AddScoped<ReleaseJobCommand>();
builder.Services.AddScoped<MarkDownloadingCommand>();
builder.Services.AddScoped<MarkPrintingCommand>();
builder.Services.AddScoped<CompleteJobCommand>();
builder.Services.AddScoped<FailJobCommand>();

// ── Background workers ────────────────────────────────────────────────────────
builder.Services.AddHostedService<ExpiryWorker>();
builder.Services.AddHostedService<CleanupWorker>();

// ── API infrastructure ────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "PrintNest API", Version = "v1" });
});

// ── CORS — locked to web origin in dev, configurable in production ────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebOrigin", policy =>
    {
        var allowedOrigins = builder.Configuration["Cors:AllowedOrigins"]?.Split(",")
            ?? new[] { "http://localhost:3000" };
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// ── Run DB migrations on startup ──────────────────────────────────────────────
// This ensures the schema is always up to date in dev and docker-compose.
// In production, consider running migrations separately before deploying.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
// ORDER MATTERS — ErrorHandling must be first to catch errors from all other middleware

app.UseMiddleware<ErrorHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("WebOrigin");

// Apply device auth middleware only to /api/v1/device/* routes.
// UseWhen (not MapWhen) — branches conditionally then REJOINS the main pipeline
// so MapControllers() is still reached after auth passes.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api/v1/device"),
    deviceApp => deviceApp.UseMiddleware<DeviceAuthMiddleware>()
);

// Apply admin auth middleware only to /api/v1/admin/* routes.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api/v1/admin"),
    adminApp => adminApp.UseMiddleware<AdminAuthMiddleware>()
);

app.MapControllers();

// ── Health check ──────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Run();

// Expose Program class for integration tests
public partial class Program { }
