using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using RiconRide.Api.Auth;
using RiconRide.Api.Config;
using RiconRide.Api.Data;
using RiconRide.Api.Features;
using RiconRide.Api.Integrations;
using RiconRide.Api.Services;

var builder = WebApplication.CreateBuilder(args);
EnvAliases.Apply(builder.Configuration);
var config = builder.Configuration;
var services = builder.Services;

services.Configure<PlatformOptions>(config.GetSection("Platform"));
services.Configure<JwtOptions>(config.GetSection("Jwt"));
services.Configure<PaystackOptions>(config.GetSection("Paystack"));
services.Configure<SmsOptions>(config.GetSection("Sms"));
services.Configure<WhatsAppOptions>(config.GetSection("WhatsApp"));
services.Configure<EmailOptions>(config.GetSection("Email"));

var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var paystack = config.GetSection("Paystack").Get<PaystackOptions>() ?? new PaystackOptions();
var platform = config.GetSection("Platform").Get<PlatformOptions>() ?? new PlatformOptions();
StartupGuards.Enforce(builder.Environment, jwt, paystack);

services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set."))
    .UseSnakeCaseNamingConvention());

services.AddSingleton(TimeProvider.System);
services.AddSingleton<TicketSigner>();
services.AddScoped<PaymentService>();
if (paystack.IsConfigured)
    services.AddHttpClient<IPaymentGateway, PaystackGateway>(c => c.Timeout = TimeSpan.FromSeconds(20));
else
    services.AddSingleton<IPaymentGateway, MockPaymentGateway>();

services.AddHttpClient("notify", c => c.Timeout = TimeSpan.FromSeconds(15));
services.AddSingleton<QueuedNotifier>();
services.AddSingleton<INotifier>(sp => sp.GetRequiredService<QueuedNotifier>());
services.AddHostedService<NotificationDispatcher>();

services.AddOperatorAuth(jwt);
services.AddProblemDetails();
services.AddRiconRideOpenApi();
services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(platform.SiteUrl.TrimEnd('/'), platform.CmsUrl.TrimEnd('/'))
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
services.Configure<ForwardedHeadersOptions>(o =>
{
    // Rate limits key on the client IP; behind Caddy/nginx that must come from
    // X-Forwarded-For. Add the proxy's address to KnownProxies when deploying.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var limitsOff = config.GetValue<bool>("RateLimiting:Disabled");
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    void Policy(string name, int perMinute) => o.AddPolicy(name, http => limitsOff
        ? RateLimitPartition.GetNoLimiter("off")
        : RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) }));
    Policy("auth", 10);        // password guessing
    Policy("lookup", 5);       // find-ticket triggers SMS sends that cost money
    Policy("public-write", 30);
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", async (AppDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.Problem(statusCode: 503, title: "Database unreachable.")).WithTags("System");

app.MapAuth();
app.MapTrips();
app.MapBookings();
app.MapGroups();
app.MapFleet();
app.MapOperations();
app.MapTransportOperators();
app.MapPayments(mockGateway: !paystack.IsConfigured);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (config.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment())) await db.Database.MigrateAsync();
    await Seeder.SeedOperatorsAsync(db, config.GetSection("Auth:SeedOperators").Get<List<SeedOperatorAccount>>() ?? []);
    if (app.Environment.IsDevelopment()) await Seeder.SeedDevelopmentAsync(db, app.Services.GetRequiredService<TimeProvider>());
    if (!paystack.IsConfigured) app.Logger.LogWarning("PAYSTACK_SECRET_KEY not set — using the MOCK payment gateway. No money moves.");
}

await app.RunAsync();

public partial class Program;
