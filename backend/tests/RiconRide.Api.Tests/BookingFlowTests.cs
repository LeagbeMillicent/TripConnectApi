using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiconRide.Api.Tests.Infrastructure;

namespace RiconRide.Api.Tests;

[Collection("api")]
public sealed class BookingFlowTests(ApiFactory api)
{
    private static object BookingBody(string tripId, int[] seats, string? session = null, string phone = "0539943305") => new
    {
        tripId,
        selectedSeatNumbers = seats,
        passengers = seats.Select((s, i) => new { fullName = $"Passenger {i}", phone, gender = "female" }),
        paymentMethod = "momo",
        mobileMoneyNumber = phone,
        mobileMoneyProvider = "MTN",
        sessionId = session,
    };

    [Fact]
    public async Task Search_returns_trip_in_contract_shape_with_availability()
    {
        var tripId = await api.NewTripAsync(daysAhead: 5, seats: 10);
        var c = api.CreateClient();
        var date = api.Clock.GetUtcNow().AddDays(5).ToString("yyyy-MM-dd");

        var list = await (await c.GetAsync($"/api/trips?from=accra&to=Kumasi&date={date}&passengers=2")).Body();
        var trip = list.EnumerateArray().Single(t => t.Str("id") == tripId);

        Assert.Equal(110m, trip.GetProperty("basePrice").GetDecimal());
        Assert.Equal("06:30 AM", trip.Str("departureTime"));
        Assert.Equal("4h 30m", trip.Str("duration"));
        Assert.Equal(10, trip.GetProperty("availableSeatsCount").GetInt32());
        Assert.Equal("Terminal A", trip.GetProperty("originStation").Str("name"));

        var tooMany = await (await c.GetAsync($"/api/trips?from=Accra&to=Kumasi&date={date}&passengers=11")).Body();
        Assert.DoesNotContain(tooMany.EnumerateArray(), t => t.Str("id") == tripId);
    }

    [Fact]
    public async Task Scheduling_a_trip_requires_a_dispatch_role()
    {
        var body = new
        {
            originCity = "Accra", destinationCity = "Kumasi", originStationId = "st-a", destinationStationId = "st-k",
            departureDate = api.Clock.GetUtcNow().AddDays(9).ToString("yyyy-MM-dd"), departureTime = "05:00 AM", duration = "9h 30m",
            basePrice = 240, operatorId = "op-test", vehicleType = "Scania (49-Seater)", totalSeatsCount = 49,
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/trips", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.As("fleet_manager")).PostAsJsonAsync("/api/trips", body)).StatusCode);

        var created = await (await api.As("dispatch_marshal")).PostAsJsonAsync("/api/trips", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var trip = await created.Body();
        Assert.StartsWith("trip-acc-kum-", trip.Str("id"));
        Assert.Equal("9h 30m", trip.Str("duration"));

        var bad = await (await api.As("dispatch_marshal")).PostAsJsonAsync("/api/trips", new { originCity = "Accra", basePrice = 10.001 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True((await bad.Body()).GetProperty("errors").TryGetProperty("basePrice", out _));
    }

    [Fact]
    public async Task Held_seats_block_other_sessions_but_convert_for_the_holder()
    {
        var tripId = await api.NewTripAsync();
        var c = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/trips/{tripId}/hold-seat", new { seatNumbers = new[] { 12, 13 }, sessionId = "sess-a" })).StatusCode);

        var other = await c.PostAsJsonAsync($"/api/trips/{tripId}/hold-seat", new { seatNumbers = new[] { 13 }, sessionId = "sess-b" });
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [12], "sess-b"))).StatusCode);

        var mine = await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [12, 13], "sess-a"));
        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        var booking = await mine.Body();
        Assert.Equal("pending_payment", booking.Str("status"));
        Assert.Equal(220m, booking.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, booking.GetProperty("qrCodeData").ValueKind); // no boarding pass before payment
    }

    [Fact]
    public async Task Concurrent_bookings_for_one_seat_sell_it_exactly_once()
    {
        var tripId = await api.NewTripAsync();
        var c = api.CreateClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [7]))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Paid_booking_flows_from_webhook_to_ticket_lookup_to_gate_check_in()
    {
        var tripId = await api.NewTripAsync(daysAhead: 0);
        var c = api.CreateClient();
        var phone = "0241000101";
        var booking = await (await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [3], phone: phone))).Body();
        var number = booking.Str("bookingNumber");
        var reference = booking.GetProperty("payment").Str("reference");

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.WebhookAsync(reference, 110_00, signatureOverride: "deadbeef")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.WebhookAsync(reference, 110_00)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.WebhookAsync(reference, 110_00)).StatusCode); // retry is harmless

        var verify = await (await c.GetAsync($"/api/payments/verify/{reference}")).Body();
        Assert.Equal("paid", verify.Str("status"));
        Assert.Single(api.Notifier.To("+233241000101"), n => n.Text.Contains(number) && n.Text.Contains("confirmed"));

        // Find my ticket: generic response, link delivered only to the phone.
        var find = await c.PostAsJsonAsync("/api/bookings/find-ticket", new { phone });
        Assert.Equal(HttpStatusCode.Accepted, find.StatusCode);
        Assert.DoesNotContain(number, await find.Content.ReadAsStringAsync());
        var link = api.Notifier.To("+233241000101").Last().Text;
        var token = link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..];

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/bookings/ticket/{number}?token=wrong")).StatusCode);
        var ticket = await (await c.GetAsync($"/api/bookings/ticket/{number}?token={token}")).Body();
        Assert.Equal("confirmed", ticket.Str("status"));
        var qr = ticket.Str("qrCodeData");
        Assert.StartsWith($"{number}|Accra-Kumasi|SEATS:3|", qr);

        var gate = await api.As("gate_marshal");
        var forged = qr[..qr.LastIndexOf("SIG:", StringComparison.Ordinal)] + "SIG:00000000000000000000";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await gate.PatchAsJsonAsync($"/api/bookings/{number}/check-in", new { qrCodeData = forged })).StatusCode);
        var boarded = await gate.PatchAsJsonAsync($"/api/bookings/{number}/check-in", new { qrCodeData = qr });
        Assert.Equal(HttpStatusCode.OK, boarded.StatusCode);
        Assert.Equal("boarded", (await boarded.Body()).Str("status"));
        Assert.Equal(HttpStatusCode.Conflict, (await gate.PatchAsJsonAsync($"/api/bookings/{number}/check-in", new { qrCodeData = qr })).StatusCode);

        var manifest = await (await gate.GetAsync($"/api/bookings?tripId={tripId}&search={phone}")).Body();
        Assert.Equal(1, manifest.GetProperty("total").GetInt32());
        Assert.Equal(phone, manifest.GetProperty("items")[0].GetProperty("passengers")[0].Str("phone"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/bookings")).StatusCode);
    }

    [Fact]
    public async Task Short_payment_is_never_fulfilled()
    {
        var tripId = await api.NewTripAsync();
        var booking = await (await api.CreateClient().PostAsJsonAsync("/api/bookings", BookingBody(tripId, [1]))).Body();
        var reference = booking.GetProperty("payment").Str("reference");

        await api.WebhookAsync(reference, 1_00);
        var verify = await (await api.CreateClient().GetAsync($"/api/payments/verify/{reference}")).Body();
        Assert.Equal("refund_due", verify.Str("status"));
    }

    [Fact]
    public async Task Unpaid_booking_releases_its_seat_and_a_late_payment_becomes_refund_due()
    {
        var tripId = await api.NewTripAsync();
        var c = api.CreateClient();
        var first = await (await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [5]))).Body();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [5]))).StatusCode);

        api.Clock.Advance(TimeSpan.FromMinutes(16)); // past the 15-minute payment window
        var second = await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [5], phone: "0201234567"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var late = first.GetProperty("payment").Str("reference");
        await api.WebhookAsync(late, 110_00);
        Assert.Equal("refund_due", (await (await c.GetAsync($"/api/payments/verify/{late}")).Body()).Str("status"));
    }

    [Fact]
    public async Task Initialize_rejects_a_client_amount_that_differs_from_the_order()
    {
        var tripId = await api.NewTripAsync();
        var c = api.CreateClient();
        var booking = await (await c.PostAsJsonAsync("/api/bookings", BookingBody(tripId, [2]))).Body();
        var body = new { referenceType = "ticket", referenceId = booking.Str("bookingNumber"), customerPhone = "0539943305", amount = 1, currency = "GHS" };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/payments/initialize", body)).StatusCode);

        var ok = await c.PostAsJsonAsync("/api/payments/initialize", body with { amount = 110 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("pending", (await ok.Body()).Str("status"));
    }

    [Fact]
    public async Task Cancelling_a_trip_with_paid_bookings_marks_refunds_instead_of_deleting()
    {
        var tripId = await api.NewTripAsync();
        var booking = await (await api.CreateClient().PostAsJsonAsync("/api/bookings", BookingBody(tripId, [9]))).Body();
        await api.WebhookAsync(booking.GetProperty("payment").Str("reference"), 110_00);

        var res = await (await api.As("station_master")).DeleteAsync($"/api/trips/{tripId}");
        var body = await res.Body();
        Assert.Equal("cancelled", body.Str("status"));
        Assert.Equal(1, body.GetProperty("refundsDue").GetInt32());
    }
}
