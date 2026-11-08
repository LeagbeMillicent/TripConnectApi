using System.Net;
using System.Net.Http.Json;
using RiconRide.Api.Domain;
using RiconRide.Api.Tests.Infrastructure;

namespace RiconRide.Api.Tests;

[Collection("api")]
public sealed class ChartersRentalsAdminTests(ApiFactory api)
{
    private string Day(int ahead) => api.Clock.GetUtcNow().AddDays(ahead).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Charter_needs_fare_approval_before_members_can_join_and_pay()
    {
        var c = api.CreateClient();
        var created = await c.PostAsJsonAsync("/api/groups", new
        {
            name = "KNUST Alumni Homecoming", tripType = "Church / Alumni", origin = "Accra (Circle Station)",
            destination = "Kumasi (KNUST Campus)", pickupLocation = "Legon Shell Landmark", pickupCoordinates = new { lat = 5.6508, lng = -0.1870 },
            travelDate = Day(20), preferredDepartureTime = "06:00 AM", targetTravelers = 2,
            organizer = new { name = "Kofi Mensah Boateng", phone = "0539943305", email = "kofi@example.com" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var group = await created.Body();
        Assert.Equal("pending_approval", group.Str("status"));
        Assert.Matches("^ACC-KUM-[A-Z2-9]{4}$", group.Str("inviteCode"));
        var id = group.Str("id");

        var member = new { name = "Ama Osei", phone = "0241002000", emergencyContact = "0539943305", paymentMethod = "momo" };
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/groups/{id}/join", member)).StatusCode);

        var dispatch = await api.As("dispatch_marshal");
        var approval = new { approvedFarePerPerson = 145, assignedCoach = "30-Seater Coaster", dispatchNotes = "Per diem covered", status = "planning" };
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.As("communications")).PatchAsJsonAsync($"/api/groups/{id}/fare-approval", approval)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await dispatch.PatchAsJsonAsync($"/api/groups/{id}/fare-approval", approval)).StatusCode);

        var joined = await (await c.PostAsJsonAsync($"/api/groups/{id}/join", member)).Body();
        Assert.Equal(145m, joined.GetProperty("fare").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/groups/{id}/join", member)).StatusCode); // same phone twice
        Assert.Equal(HttpStatusCode.Conflict, (await dispatch.PatchAsJsonAsync($"/api/groups/{id}/fare-approval", approval with { approvedFarePerPerson = 100 })).StatusCode);

        await api.WebhookAsync(joined.GetProperty("payment").Str("reference"), 145_00);
        var publicView = await c.GetAsync($"/api/groups/code/{group.Str("inviteCode")}");
        var view = await publicView.Body();
        Assert.Equal(1, view.GetProperty("confirmedMembers").GetInt32());
        Assert.Equal(1, view.GetProperty("seatsRemaining").GetInt32());
        Assert.Equal("Kofi", view.Str("organizerFirstName"));
        var raw = await publicView.Content.ReadAsStringAsync();
        Assert.DoesNotContain("0539943305", raw); // organiser phone is not public
        Assert.DoesNotContain("kofi@example.com", raw);
    }

    [Fact]
    public async Task Rental_blocks_overlapping_dates_and_deposit_release_refunds_net_of_deductions()
    {
        var fleet = await api.As("fleet_manager");
        var plate = "GT " + Codes.Digits(4) + "-26";
        var vehicle = await (await fleet.PostAsJsonAsync("/api/vehicles", new
        {
            name = "Toyota Land Cruiser Prado TX", category = "SUV", licensePlate = plate, dailyPrice = 950, weeklyPrice = 5800,
            securityDeposit = 1000, withDriverAllowed = true, selfDriveAllowed = true, driverFeePerDay = 200,
            features = new[] { "4x4 Drive" }, dvlaRoadworthinessExpiry = Day(400),
        })).Body();
        Assert.Equal(HttpStatusCode.Conflict, (await fleet.PostAsJsonAsync("/api/vehicles", new
        {
            name = "Dup", category = "SUV", licensePlate = plate, dailyPrice = 1, securityDeposit = 0, selfDriveAllowed = true, dvlaRoadworthinessExpiry = Day(400),
        })).StatusCode);

        var c = api.CreateClient();
        var rentalBody = new
        {
            vehicleId = vehicle.Str("id"), startDate = Day(0), endDate = Day(7), pickupPoint = "Airport", driverOption = "with-driver",
            customer = new { name = "Yaw Asante", phone = "0551234567", email = (string?)null }, paymentMethod = "momo",
        };
        var rental = await (await c.PostAsJsonAsync("/api/rentals", rentalBody)).Body();
        // 8 days = 1 week (5800) + 1 day (950); driver 8 × 200; deposit 1000.
        Assert.Equal(5800m + 950m, rental.GetProperty("rentalAmount").GetDecimal());
        Assert.Equal(1600m, rental.GetProperty("driverFee").GetDecimal());
        Assert.Equal(9350m, rental.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/rentals", rentalBody with { startDate = Day(3), endDate = Day(4) })).StatusCode);

        var available = await (await c.GetAsync($"/api/vehicles?availability=available&startDate={Day(2)}&endDate={Day(2)}")).Body();
        Assert.DoesNotContain(available.EnumerateArray(), v => v.Str("id") == vehicle.Str("id"));

        var id = rental.Str("id");
        var settle = new { action = "deduct", deductions = new[] { new { reason = "Fuel top-up", amount = 150m } }, inspectionNotes = "Quarter tank" };
        Assert.Equal(HttpStatusCode.Conflict, (await fleet.PatchAsJsonAsync($"/api/rentals/{id}/deposit", settle)).StatusCode); // unpaid: nothing held

        var reference = rental.GetProperty("payment").Str("reference");
        await api.WebhookAsync(reference, 9350_00);
        var done = await fleet.PatchAsJsonAsync($"/api/rentals/{id}/deposit", settle);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = await done.Body();
        Assert.Equal("partially_released", body.Str("depositStatus"));
        Assert.Equal(850m, body.GetProperty("depositRefunded").GetDecimal());
        Assert.Contains(api.Gateway.Refunds, r => r.Reference == reference && r.Amount == 850_00);

        Assert.Equal(HttpStatusCode.Conflict, (await fleet.PatchAsJsonAsync($"/api/rentals/{id}/deposit", settle)).StatusCode); // never twice
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var c = api.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email = "network_manager@test.riconride.com", password = "nope-nope-nope" })).StatusCode);
        var locked = await c.PostAsJsonAsync("/api/auth/login", new { email = "network_manager@test.riconride.com", password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.riconride.com", password = "whatever-123" })).StatusCode);
    }

    [Fact]
    public async Task Login_sets_an_httponly_cookie_that_authenticates_the_cms()
    {
        var c = api.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { email = "executive@test.riconride.com", password = ApiFactory.Password });
        var cookie = res.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("rr_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        // The test client keeps cookies, so this call is authenticated by cookie alone.
        var stats = await c.GetAsync("/api/admin/dashboard-stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        var body = await stats.Body();
        Assert.True(body.GetProperty("grossVolume").GetDecimal() >= body.GetProperty("grossVolumeToday").GetDecimal());
    }

    [Fact]
    public async Task Terminals_and_content_are_public_to_read_and_role_gated_to_write()
    {
        // station_master, not network_manager: the lockout test deliberately locks that account.
        var net = await api.As("station_master");
        var t = await net.PostAsJsonAsync("/api/terminals", new { name = "Takoradi Market Circle " + Codes.Digits(4), city = "Takoradi", address = "Market Circle", latitude = 4.9, longitude = -1.76, marshalPhone = "0244000000" });
        Assert.Equal(HttpStatusCode.Created, t.StatusCode);
        Assert.Contains("google.com/maps/dir", (await t.Body()).Str("directionsUrl"));

        var comms = await api.As("communications");
        var faq = new { question = "Can I pay with Telecel Cash?", answer = "Yes.", category = "Payments" };
        Assert.Equal(HttpStatusCode.Forbidden, (await net.PostAsJsonAsync("/api/content/faqs", faq)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await comms.PostAsJsonAsync("/api/content/faqs", faq)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await comms.PostAsJsonAsync("/api/content/banners", new { title = "N1 diversion", message = "Expect delays at Kasoa.", severity = "warning" })).StatusCode);

        var c = api.CreateClient();
        Assert.Contains((await (await c.GetAsync("/api/content/faqs")).Body()).EnumerateArray(), f => f.Str("question") == faq.question);
        Assert.Contains((await (await c.GetAsync("/api/content/banners")).Body()).EnumerateArray(), b => b.Str("title") == "N1 diversion");
    }
}

[Collection("api")]
public sealed class TransportOperatorTests(ApiFactory api)
{
    [Fact]
    public async Task Operators_are_admin_managed_and_a_deactivated_one_cannot_get_new_trips()
    {
        var name = "Metro Mass " + Codes.Digits(4);
        var body = new { name, isVerified = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.As("dispatch_marshal")).PostAsJsonAsync("/api/operators", body)).StatusCode);

        var admin = await api.As("system_admin");
        var created = await admin.PostAsJsonAsync("/api/operators", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var op = await created.Body();
        var id = op.Str("id");
        Assert.StartsWith("op-metro-mass-", id);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, op.GetProperty("rating").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/operators", new { id, name })).StatusCode);

        var trip = new
        {
            originCity = "Accra", destinationCity = "Kumasi", originStationId = "st-a", destinationStationId = "st-k",
            departureDate = api.Clock.GetUtcNow().AddDays(4).ToString("yyyy-MM-dd"), departureTime = "07:00 AM", duration = "4h",
            basePrice = 120, operatorId = id, vehicleType = "Coach", totalSeatsCount = 30,
        };
        var dispatch = await api.As("dispatch_marshal");
        Assert.Equal(HttpStatusCode.Created, (await dispatch.PostAsJsonAsync("/api/trips", trip)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/operators/{id}", new { isActive = false })).StatusCode);
        Assert.DoesNotContain((await (await api.CreateClient().GetAsync("/api/operators")).Body()).EnumerateArray(), o => o.Str("id") == id);
        var refused = await dispatch.PostAsJsonAsync("/api/trips", trip);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await refused.Body()).GetProperty("errors").TryGetProperty("operatorId", out _));
    }
}

public sealed class DomainTests
{
    [Theory]
    [InlineData("0539943305", "+233539943305")]
    [InlineData("+233 53 994 3305", "+233539943305")]
    [InlineData("233241002000", "+233241002000")]
    [InlineData("020-123-4567", "+233201234567")]
    public void Ghana_phones_normalise_to_e164(string raw, string expected)
    {
        Assert.True(GhanaPhone.TryNormalize(raw, out var e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("0739943305")]
    [InlineData("+2335399433051")]
    public void Non_ghana_mobile_numbers_are_rejected(string raw) => Assert.False(GhanaPhone.TryNormalize(raw, out _));

    [Fact]
    public void Money_rejects_sub_pesewa_amounts_and_converts_exactly()
    {
        Assert.False(Money.IsValidAmount(10.005m));
        Assert.True(Money.IsValidAmount(10.05m));
        Assert.Equal(1005, Money.ToPesewas(10.05m));
        Assert.Equal("GH₵220", Money.Format(220_00));
        Assert.Equal("GH₵12.50", Money.Format(12_50));
    }

    [Theory]
    [InlineData("4h 30m", 270)]
    [InlineData("9h", 540)]
    [InlineData("45m", 45)]
    public void Durations_round_trip(string text, int minutes)
    {
        Assert.True(Formats.TryParseDuration(text, out var m));
        Assert.Equal(minutes, m);
        Assert.Equal(text, Formats.Duration(m));
    }
}
