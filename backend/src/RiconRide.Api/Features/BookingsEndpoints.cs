using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RiconRide.Api.Auth;
using RiconRide.Api.Config;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;
using RiconRide.Api.Integrations;
using RiconRide.Api.Services;

namespace RiconRide.Api.Features;

public static class BookingsEndpoints
{
    public sealed record PassengerIn(string? FullName, string? Phone, string? Gender);

    public sealed record CreateBooking(
        string? TripId, List<int>? SelectedSeatNumbers, List<PassengerIn>? Passengers, string? PaymentMethod,
        string? MobileMoneyNumber, string? MobileMoneyProvider, string? CustomerEmail, string? SessionId);

    public sealed record FindTicket(string? Phone);

    public sealed record CheckIn(string? QrCodeData);

    public static readonly IReadOnlyDictionary<string, BookingStatus> Statuses = new Dictionary<string, BookingStatus>(StringComparer.OrdinalIgnoreCase)
    {
        ["pending_payment"] = BookingStatus.PendingPayment, ["confirmed"] = BookingStatus.Confirmed,
        ["boarded"] = BookingStatus.Boarded, ["expired"] = BookingStatus.Expired,
        ["cancelled"] = BookingStatus.Cancelled, ["refund_due"] = BookingStatus.RefundDue,
    };

    public static string Wire(BookingStatus s) => Statuses.First(kv => kv.Value == s).Key;

    public static void MapBookings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/bookings").WithTags("Bookings");

        g.MapPost("/", Create).RequireRateLimiting("public-write");

        g.MapPost("/find-ticket", async (FindTicket req, AppDbContext db, INotifier notifier, TicketSigner tickets, TimeProvider clock) =>
        {
            var v = new Validator();
            var phone = v.Phone("phone", req.Phone);
            if (!v.IsValid) return v.Problem();

            var today = GhanaClock.Today(clock);
            var found = await db.Bookings.Include(b => b.Trip)
                .Where(b => b.Status == BookingStatus.Confirmed && b.Trip.DepartureDate >= today)
                .Where(b => b.ContactPhone == phone || b.Passengers.Any(p => p.Phone == phone))
                .OrderBy(b => b.Trip.DepartureDate).Take(5).ToListAsync();
            foreach (var b in found)
                notifier.Enqueue(new Notification(phone, null, $"RiconRide ticket {b.BookingNumber}",
                    $"RiconRide: {b.Trip.OriginCity} to {b.Trip.DestinationCity}, {Formats.Date(b.Trip.DepartureDate)} " +
                    $"{Formats.Time(b.Trip.DepartureTime)}. Boarding pass: {tickets.TicketLink(b.BookingNumber)}"));
            // Same answer whether or not tickets exist: the response must not reveal
            // whether a phone number has travelled with us. Links go only to the phone itself.
            return Results.Accepted(value: new { message = "If this number has upcoming tickets, boarding links have been sent to it by SMS and WhatsApp." });
        }).RequireRateLimiting("lookup");

        g.MapGet("/ticket/{bookingNumber}", async (string bookingNumber, string? token, AppDbContext db, TicketSigner tickets) =>
        {
            // Opened from the SMS/WhatsApp link; the token proves the holder received it.
            if (!tickets.Verify(bookingNumber, token)) return Problems.NotFound("Ticket");
            var b = await Full(db).SingleOrDefaultAsync(x => x.BookingNumber == bookingNumber);
            return b is null ? Problems.NotFound("Ticket") : Results.Ok(Ticket(b, tickets));
        });

        g.MapGet("/", async (string? search, string? status, string? tripId, string? date, int? page, int? limit, AppDbContext db, TimeProvider clock) =>
        {
            var v = new Validator();
            BookingStatus? st = status is null ? null : v.OneOf("status", status, Statuses);
            DateOnly? day = date is null ? null : v.Date("date", date);
            if (!v.IsValid) return v.Problem();
            await Expiry.PurgeAsync(db, clock);

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(limit ?? 25, 1, 100);
            var q = Full(db);
            if (st is not null) q = q.Where(b => b.Status == st);
            if (!string.IsNullOrWhiteSpace(tripId)) q = q.Where(b => b.TripId == tripId);
            if (day is not null) q = q.Where(b => b.Trip.DepartureDate == day);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                var asPhone = GhanaPhone.TryNormalize(s, out var e164) ? e164 : null;
                var like = "%" + s.Replace("%", "").Replace("_", "") + "%";
                q = q.Where(b => EF.Functions.ILike(b.BookingNumber, like)
                                 || b.Passengers.Any(x => EF.Functions.ILike(x.FullName, like) || x.Phone == asPhone)
                                 || b.ContactPhone == asPhone);
            }
            var total = await q.CountAsync();
            var rows = await q.OrderBy(b => b.Trip.DepartureDate).ThenBy(b => b.Trip.DepartureTime).ThenBy(b => b.CreatedAt)
                .Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items = rows.Select(ManifestRow), page = p, limit = size, total });
        }).RequireRoles(OperatorRole.DispatchMarshal, OperatorRole.StationMaster, OperatorRole.GateMarshal);

        g.MapPatch("/{id}/check-in", async (string id, CheckIn? req, ClaimsPrincipal user, AppDbContext db, TicketSigner tickets, TimeProvider clock) =>
        {
            // A scanned QR must carry a valid signature; typing a booking number
            // is the manual fallback when a phone screen won't scan.
            Booking? b;
            if (!string.IsNullOrWhiteSpace(req?.QrCodeData))
            {
                var number = tickets.ReadQr(req.QrCodeData);
                if (number is null) return Problems.Unprocessable("QR code is not a genuine RiconRide ticket.");
                b = await Full(db).SingleOrDefaultAsync(x => x.BookingNumber == number);
                if (b is not null && b.BookingNumber != id && b.Id.ToString() != id)
                    return Problems.Unprocessable("QR code belongs to a different booking than the one requested.");
            }
            else
            {
                b = Guid.TryParse(id, out var gid)
                    ? await Full(db).SingleOrDefaultAsync(x => x.Id == gid)
                    : await Full(db).SingleOrDefaultAsync(x => x.BookingNumber == id);
            }
            if (b is null) return Problems.NotFound("Booking");

            if (b.Status == BookingStatus.Boarded)
                return Problems.Conflict($"Already boarded at {b.CheckedInAt:HH:mm} UTC.");
            if (b.Status != BookingStatus.Confirmed)
                return Problems.Conflict($"Ticket is not valid for boarding (status: {Wire(b.Status)}).");
            if (b.Trip.Status is TripStatus.Cancelled or TripStatus.Arrived)
                return Problems.Conflict("This trip is not boarding.");
            if (b.Trip.DepartureDate != GhanaClock.Today(clock))
                return Problems.Conflict($"Ticket is for {Formats.Date(b.Trip.DepartureDate)}, not today.");

            b.Status = BookingStatus.Boarded;
            b.CheckedInAt = clock.GetUtcNow();
            b.CheckedInBy = user.OperatorId();
            await db.SaveChangesAsync();
            return Results.Ok(ManifestRow(b));
        }).RequireRoles(OperatorRole.GateMarshal, OperatorRole.StationMaster, OperatorRole.DispatchMarshal);
    }

    private static async Task<IResult> Create(CreateBooking req, AppDbContext db, PaymentService payments,
        TicketSigner tickets, TimeProvider clock, IOptions<PlatformOptions> opt, CancellationToken ct)
    {
        var v = new Validator().Require("tripId", req.TripId).Optional("customerEmail", req.CustomerEmail, 254);
        var seats = req.SelectedSeatNumbers ?? [];
        var people = req.Passengers ?? [];
        if (seats.Count == 0) v.Add("selectedSeatNumbers", "must contain at least one seat.");
        if (seats.Count > 10) v.Add("selectedSeatNumbers", "at most 10 seats per booking.");
        if (seats.Distinct().Count() != seats.Count) v.Add("selectedSeatNumbers", "must not repeat a seat.");
        if (people.Count != seats.Count) v.Add("passengers", "must list exactly one passenger per selected seat.");
        var normalized = people.Select((p, i) =>
        {
            v.Require($"passengers[{i}].fullName", p.FullName, 120);
            var ph = v.Phone($"passengers[{i}].phone", p.Phone);
            var gender = v.OptionalChoice($"passengers[{i}].gender", p.Gender, Choices.Genders);
            return (Name: p.FullName?.Trim() ?? "", Phone: ph, Gender: gender);
        }).ToList();
        var method = v.Choice("paymentMethod", req.PaymentMethod, Choices.PaymentMethods);
        string? momo = null, provider = null;
        if (method == "momo")
        {
            momo = v.Phone("mobileMoneyNumber", req.MobileMoneyNumber);
            provider = v.Choice("mobileMoneyProvider", req.MobileMoneyProvider, Choices.MomoProviders);
        }
        if (!v.IsValid) return v.Problem();

        var trip = await db.Trips.FindAsync([req.TripId], ct);
        if (trip is null) return Problems.NotFound("Trip");
        if (trip.Status is not (TripStatus.Scheduled or TripStatus.Boarding) || trip.DepartureDate < GhanaClock.Today(clock))
            return Problems.Conflict("This trip is no longer open for booking.");
        if (seats.Any(s => s < 1 || s > trip.TotalSeats))
            return new Validator().Add("selectedSeatNumbers", $"seats must be between 1 and {trip.TotalSeats}.").Problem();

        await Expiry.PurgeAsync(db, clock, ct);
        var now = clock.GetUtcNow();
        var session = req.SessionId?.Trim();
        var booking = new Booking
        {
            BookingNumber = Codes.BookingNumber(now), TripId = trip.Id,
            ContactPhone = momo ?? normalized[0].Phone,
            TotalPesewas = trip.BasePricePesewas * seats.Count, // price fixed now; later fare edits don't change it
            PaymentMethod = method, MobileMoneyNumber = momo, MobileMoneyProvider = provider,
            ExpiresAt = now.AddMinutes(opt.Value.PaymentWindowMinutes),
            Passengers = seats.Zip(normalized, (s, p) => new BookingPassenger { SeatNumber = s, FullName = p.Name, Phone = p.Phone, Gender = p.Gender }).ToList(),
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var taken = await db.SeatReservations.Where(r => r.TripId == trip.Id && seats.Contains(r.SeatNumber))
                .ToListAsync(ct);
            var others = taken.Where(r => session is null || r.HoldSessionId != session || r.BookingId != null).Select(r => r.SeatNumber).ToList();
            if (others.Count > 0) return Problems.Conflict("Some seats are already taken.", new { unavailableSeats = others.Order() });
            db.SeatReservations.RemoveRange(taken); // this session's own holds become the booking's seats
            db.Bookings.Add(booking);
            db.SeatReservations.AddRange(seats.Select(s => new SeatReservation { TripId = trip.Id, SeatNumber = s, BookingId = booking.Id, ExpiresAt = booking.ExpiresAt }));
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
            {
                return Problems.Conflict("Some seats were just taken by another traveller. Pick again.");
            }
        }

        var start = await payments.StartAsync(PaymentKind.Ticket, booking.Id, booking.TotalPesewas, booking.ContactPhone,
            req.CustomerEmail?.Trim(), provider, ct);
        var full = await Full(db).SingleAsync(b => b.Id == booking.Id, ct);
        var body = new Dictionary<string, object?>((IDictionary<string, object?>)Ticket(full, tickets))
        {
            ["payment"] = PaymentsEndpoints.Present(start.Payment),
            ["paymentExpiresAt"] = booking.ExpiresAt,
        };
        if (start.Error is not null) body["paymentError"] = start.Error;
        return Results.Created($"/api/bookings/ticket/{booking.BookingNumber}", body);
    }

    private static IQueryable<Booking> Full(AppDbContext db) =>
        db.Bookings.Include(b => b.Passengers).Include(b => b.Trip).ThenInclude(t => t.OriginTerminal)
            .Include(b => b.Trip).ThenInclude(t => t.DestinationTerminal);

    /// <summary>Traveller-facing ticket. The QR appears only once the booking is paid.</summary>
    private static IDictionary<string, object?> Ticket(Booking b, TicketSigner tickets) => new Dictionary<string, object?>
    {
        ["bookingNumber"] = b.BookingNumber,
        ["status"] = Wire(b.Status),
        ["totalAmount"] = Money.ToCedis(b.TotalPesewas),
        ["qrCodeData"] = b.Status is BookingStatus.Confirmed or BookingStatus.Boarded ? tickets.QrData(b) : null,
        ["departureTime"] = Formats.Time(b.Trip.DepartureTime),
        ["departureDate"] = Formats.Date(b.Trip.DepartureDate),
        ["originStation"] = b.Trip.OriginTerminal.Name,
        ["destinationStation"] = b.Trip.DestinationTerminal.Name,
        ["route"] = $"{b.Trip.OriginCity}-{b.Trip.DestinationCity}",
        ["passengers"] = b.Passengers.OrderBy(p => p.SeatNumber).Select(p => new { fullName = p.FullName, seatNumber = p.SeatNumber }),
    };

    private static object ManifestRow(Booking b) => new
    {
        id = b.Id,
        bookingNumber = b.BookingNumber,
        status = Wire(b.Status),
        trip = new { id = b.TripId, route = $"{b.Trip.OriginCity}-{b.Trip.DestinationCity}", departureDate = Formats.Date(b.Trip.DepartureDate), departureTime = Formats.Time(b.Trip.DepartureTime) },
        contactPhone = GhanaPhone.ToLocal(b.ContactPhone),
        passengers = b.Passengers.OrderBy(p => p.SeatNumber).Select(p => new { fullName = p.FullName, phone = GhanaPhone.ToLocal(p.Phone), gender = p.Gender, seatNumber = p.SeatNumber }),
        totalAmount = Money.ToCedis(b.TotalPesewas),
        checkedInAt = b.CheckedInAt,
        createdAt = b.CreatedAt,
    };
}
