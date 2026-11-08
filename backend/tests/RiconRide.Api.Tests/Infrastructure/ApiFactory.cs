using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;
using RiconRide.Api.Integrations;
using Testcontainers.PostgreSql;

namespace RiconRide.Api.Tests.Infrastructure;

public sealed class MutableClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class RecordingNotifier : INotifier
{
    public ConcurrentQueue<Notification> Sent { get; } = new();
    public void Enqueue(Notification n) => Sent.Enqueue(n);
    public IEnumerable<Notification> To(string phoneE164) => Sent.Where(n => n.PhoneE164 == phoneE164);
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;

/// <summary>
/// One fresh Postgres container per test run, so no state accumulates between
/// runs. Tests in the "api" collection run sequentially and each creates its own
/// trips/vehicles, so no test asserts on a number another test can move.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly string[] Roles =
        ["system_admin", "dispatch_marshal", "station_master", "gate_marshal", "fleet_manager", "network_manager", "communications", "executive"];

    public const string Password = "test-password-123";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public MutableClock Clock { get; } = new();
    public RecordingNotifier Notifier { get; } = new();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _ = Server; // boots the app: runs migrations and seeds operators
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TransportOperators.Add(new TransportOperator { Id = "op-test", Name = "Test Coaches", IsVerified = true });
        db.Terminals.AddRange(
            new Terminal { Id = "st-a", Name = "Terminal A", City = "Accra", Address = "Circle", Latitude = 5.57, Longitude = -0.21 },
            new Terminal { Id = "st-k", Name = "Terminal K", City = "Kumasi", Address = "Asafo" });
        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _pg.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:Disabled", "true");
        builder.UseSetting("Paystack:SecretKey", "");
        for (var i = 0; i < Roles.Length; i++)
        {
            builder.UseSetting($"Auth:SeedOperators:{i}:Email", $"{Roles[i]}@test.riconride.com");
            builder.UseSetting($"Auth:SeedOperators:{i}:Password", Password);
            builder.UseSetting($"Auth:SeedOperators:{i}:FullName", Roles[i]);
            builder.UseSetting($"Auth:SeedOperators:{i}:Role", Roles[i]);
        }
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<INotifier>();
            s.AddSingleton<INotifier>(Notifier);
        });
    }

    public MockPaymentGateway Gateway => (MockPaymentGateway)Services.GetRequiredService<IPaymentGateway>();

    public async Task<HttpClient> As(string role)
    {
        var c = CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { email = $"{role}@test.riconride.com", password = Password });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>Inserts a trip unique to the calling test.</summary>
    public async Task<string> NewTripAsync(int daysAhead = 2, int seats = 20, long pricePesewas = 110_00)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = "trip-t-" + Guid.NewGuid().ToString("N")[..10];
        db.Trips.Add(new Trip
        {
            Id = id, OriginCity = "Accra", DestinationCity = "Kumasi", OriginTerminalId = "st-a", DestinationTerminalId = "st-k",
            DepartureDate = GhanaClock.Today(Clock).AddDays(daysAhead), DepartureTime = new TimeOnly(6, 30), DurationMinutes = 270,
            BasePricePesewas = pricePesewas, OperatorId = "op-test", VehicleType = "Coach", TotalSeats = seats,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Delivers a gateway webhook exactly as Paystack would: raw JSON + HMAC-SHA512 header.</summary>
    public async Task<HttpResponseMessage> WebhookAsync(string reference, long amountPesewas, string currency = "GHS", string? signatureOverride = null)
    {
        var json = JsonSerializer.Serialize(new { @event = "charge.success", data = new { reference, amount = amountPesewas, currency, status = "success" } });
        var bytes = Encoding.UTF8.GetBytes(json);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook") { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("x-paystack-signature", signatureOverride ?? WebhookSignature.Compute(MockPaymentGateway.DevSigningKey, bytes));
        return await CreateClient().SendAsync(req);
    }
}

public static class Json
{
    public static async Task<JsonElement> Body(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
}
