using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RiconRide.Api.Auth;
using RiconRide.Api.Config;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;
using RiconRide.Api.Integrations;
using RiconRide.Api.Services;

namespace RiconRide.Api.Features;

public static class TripsEndpoints
{
    public sealed record TripWrite(
        string? OriginCity, string? DestinationCity, string? OriginStationId, string? DestinationStationId,
        string? DepartureDate, string? DepartureTime, string? Duration, decimal? BasePrice, string? OperatorId,
        string? VehicleType, int? TotalSeatsCount, List<string>? Amenities, List<string>? PickupLandmarks, string? Status);

    public sealed record HoldRequest(List<int>? SeatNumbers, string? SessionId);

    private static readonly IReadOnlyDictionary<string, TripStatus> Statuses = new Dictionary<string, TripStatus>(StringComparer.OrdinalIgnoreCase)
    {
        ["scheduled"] = TripStatus.Scheduled, ["boarding"] = TripStatus.Boarding,
        ["departed"] = TripStatus.Departed, ["arrived"] = TripStatus.Arrived,
    };

    private const int MaxSeatsPerHold = 10;

    public static void MapTrips(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/trips").WithTags("Trips");

        g.MapGet("/", async (string? from, string? to, string? date, int? passengers, string? @operator,
            AppDbContext db, TimeProvider clock) =>
        {
            var v = new Validator();
            DateOnly? day = date is null ? null : v.Date("date", date);
            if (passengers is < 1 or > 49) v.Add("passengers", "must be between 1 and 49.");
            if (!v.IsValid) return v.Problem();

            await Expiry.PurgeAsync(db, clock);
            var today = GhanaClock.Today(clock);
            var q = db.Trips.Include(t => t.OriginTerminal).Include(t => t.DestinationTerminal).Include(t => t.Operator)
                .Where(t => t.Status == TripStatus.Scheduled || t.Status == TripStatus.Boarding)
                .Where(t => t.DepartureDate >= today);
            if (!string.IsNullOrWhiteSpace(from)) q = q.Where(t => EF.Functions.ILike(t.OriginCity, from.Trim()));
            if (!string.IsNullOrWhiteSpace(to)) q = q.Where(t => EF.Functions.ILike(t.DestinationCity, to.Trim()));
            if (day is not null) q = q.Where(t => t.DepartureDate == day);
            if (!string.IsNullOrWhiteSpace(@operator)) q = q.Where(t => t.OperatorId == @operator);

            var rows = await q.OrderBy(t => t.DepartureDate).ThenBy(t => t.DepartureTime).Take(200)
                .Select(t => new { Trip = t, Taken = db.SeatReservations.Count(r => r.TripId == t.Id) })
                .ToListAsync();
            var need = passengers ?? 1;
            return Results.Ok(rows.Where(r => r.Trip.TotalSeats - r.Taken >= need).Select(r => Present(r.Trip, r.Taken)));
        });

        g.MapGet("/{id}", async (string id, AppDbContext db, TimeProvider clock) =>
        {
            await Expiry.PurgeAsync(db, clock);
            var trip = await Load(db).SingleOrDefaultAsync(t => t.Id == id);
            if (trip is null) return Problems.NotFound("Trip");
            var taken = await db.SeatReservations.Where(r => r.TripId == id).Select(r => r.SeatNumber).ToListAsync();
            var set = taken.ToHashSet();
            // Seat map exposes only occupancy — never who sits where.
            var seats = Enumerable.Range(1, trip.TotalSeats).Select(n => new { seatNumber = n, isOccupied = set.Contains(n) });
            var dto = Present(trip, taken.Count);
            return Results.Ok(new { trip = dto, seats, pickupLandmarks = trip.PickupLandmarks });
        });

        g.MapPost("/", async (TripWrite req, AppDbContext db) =>
        {
            var v = new Validator()
                .Require("originCity", req.OriginCity, 80).Require("destinationCity", req.DestinationCity, 80)
                .Require("originStationId", req.OriginStationId).Require("destinationStationId", req.DestinationStationId)
                .Require("operatorId", req.OperatorId).Require("vehicleType", req.VehicleType, 120);
            var date = v.Date("departureDate", req.DepartureDate);
            var time = v.Time("departureTime", req.DepartureTime);
            if (!Formats.TryParseDuration(req.Duration, out var minutes)) v.Add("duration", "must look like 4h 30m.");
            var price = v.Amount("basePrice", req.BasePrice);
            if (req.TotalSeatsCount is null or < 1 or > 100) v.Add("totalSeatsCount", "must be between 1 and 100.");
            await CheckRefs(v, db, req.OriginStationId, req.DestinationStationId, req.OperatorId);
            if (!v.IsValid) return v.Problem();

            var trip = new Trip
            {
                Id = $"trip-{Codes.CityPrefix(req.OriginCity!).ToLowerInvariant()}-{Codes.CityPrefix(req.DestinationCity!).ToLowerInvariant()}-{Codes.Alnum(6).ToLowerInvariant()}",
                OriginCity = req.OriginCity!.Trim(), DestinationCity = req.DestinationCity!.Trim(),
                OriginTerminalId = req.OriginStationId!, DestinationTerminalId = req.DestinationStationId!,
                DepartureDate = date, DepartureTime = time, DurationMinutes = minutes, BasePricePesewas = price,
                OperatorId = req.OperatorId!, VehicleType = req.VehicleType!.Trim(), TotalSeats = req.TotalSeatsCount!.Value,
                Amenities = Clean(req.Amenities), PickupLandmarks = Clean(req.PickupLandmarks),
            };
            db.Trips.Add(trip);
            await db.SaveChangesAsync();
            var saved = await Load(db).SingleAsync(t => t.Id == trip.Id);
            return Results.Created($"/api/trips/{trip.Id}", Present(saved, 0));
        }).RequireRoles(OperatorRole.DispatchMarshal, OperatorRole.StationMaster);

        g.MapPatch("/{id}", async (string id, TripWrite req, AppDbContext db, TimeProvider clock) =>
        {
            var trip = await db.Trips.FindAsync(id);
            if (trip is null) return Problems.NotFound("Trip");
            if (trip.Status == TripStatus.Cancelled) return Problems.Conflict("Trip is cancelled and cannot be changed.");

            var v = new Validator().Optional("vehicleType", req.VehicleType, 120);
            if (req.DepartureDate is not null) trip.DepartureDate = v.Date("departureDate", req.DepartureDate);
            if (req.DepartureTime is not null) trip.DepartureTime = v.Time("departureTime", req.DepartureTime);
            if (req.Duration is not null)
            {
                if (Formats.TryParseDuration(req.Duration, out var m)) trip.DurationMinutes = m;
                else v.Add("duration", "must look like 4h 30m.");
            }
            // Price changes apply to new bookings only; confirmed tickets keep what was paid.
            if (req.BasePrice is not null) trip.BasePricePesewas = v.Amount("basePrice", req.BasePrice);
            if (req.Status is not null) trip.Status = v.OneOf("status", req.Status, Statuses);
            if (req.TotalSeatsCount is not null)
            {
                await Expiry.PurgeAsync(db, clock);
                var highest = await db.SeatReservations.Where(r => r.TripId == id).MaxAsync(r => (int?)r.SeatNumber) ?? 0;
                if (req.TotalSeatsCount is < 1 or > 100) v.Add("totalSeatsCount", "must be between 1 and 100.");
                else if (req.TotalSeatsCount < highest) v.Add("totalSeatsCount", $"cannot drop below seat {highest}, which is taken.");
                else trip.TotalSeats = req.TotalSeatsCount.Value;
            }
            await CheckRefs(v, db, req.OriginStationId, req.DestinationStationId, req.OperatorId);
            if (!v.IsValid) return v.Problem();
            if (req.OriginStationId is not null) trip.OriginTerminalId = req.OriginStationId;
            if (req.DestinationStationId is not null) trip.DestinationTerminalId = req.DestinationStationId;
            if (req.OperatorId is not null) trip.OperatorId = req.OperatorId;
            if (req.OriginCity is not null) trip.OriginCity = req.OriginCity.Trim();
            if (req.DestinationCity is not null) trip.DestinationCity = req.DestinationCity.Trim();
            if (req.VehicleType is not null) trip.VehicleType = req.VehicleType.Trim();
            if (req.Amenities is not null) trip.Amenities = Clean(req.Amenities);
            if (req.PickupLandmarks is not null) trip.PickupLandmarks = Clean(req.PickupLandmarks);
            await db.SaveChangesAsync();
            var taken = await db.SeatReservations.CountAsync(r => r.TripId == id);
            return Results.Ok(Present(await Load(db).SingleAsync(t => t.Id == id), taken));
        }).RequireRoles(OperatorRole.DispatchMarshal, OperatorRole.StationMaster);

        g.MapDelete("/{id}", async (string id, AppDbContext db, INotifier notifier) =>
        {
            var trip = await db.Trips.FindAsync(id);
            if (trip is null) return Problems.NotFound("Trip");
            var bookings = await db.Bookings.Where(b => b.TripId == id).ToListAsync();
            if (bookings.Count == 0)
            {
                // Nothing references it, so a typo'd schedule can simply be removed.
                db.Trips.Remove(trip);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
            // With bookings it is cancelled, never deleted: paid passengers are owed refunds.
            trip.Status = TripStatus.Cancelled;
            var refunds = 0;
            foreach (var b in bookings)
            {
                if (b.Status is BookingStatus.Confirmed or BookingStatus.Boarded)
                {
                    b.Status = BookingStatus.RefundDue;
                    refunds++;
                    notifier.Enqueue(new Notification(b.ContactPhone, null, "RiconRide trip cancelled",
                        $"RiconRide: your trip {trip.OriginCity} to {trip.DestinationCity} on {Formats.Date(trip.DepartureDate)} " +
                        $"(ticket {b.BookingNumber}) has been cancelled by the operator. A refund of {Money.Format(b.TotalPesewas)} is being processed."));
                }
                else if (b.Status == BookingStatus.PendingPayment) b.Status = BookingStatus.Cancelled;
            }
            await db.SeatReservations.Where(r => r.TripId == id).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            return Results.Ok(new { id, status = "cancelled", refundsDue = refunds });
        }).RequireRoles(OperatorRole.DispatchMarshal, OperatorRole.StationMaster);

        g.MapPost("/{id}/hold-seat", async (string id, HoldRequest req, AppDbContext db, TimeProvider clock, IOptions<PlatformOptions> opt) =>
        {
            var v = new Validator().Require("sessionId", req.SessionId, 100);
            var seats = req.SeatNumbers?.Distinct().ToList() ?? [];
            if (seats.Count == 0) v.Add("seatNumbers", "must contain at least one seat.");
            if (seats.Count > MaxSeatsPerHold) v.Add("seatNumbers", $"at most {MaxSeatsPerHold} seats per hold.");
            if (!v.IsValid) return v.Problem();

            var trip = await db.Trips.FindAsync(id);
            if (trip is null) return Problems.NotFound("Trip");
            if (trip.Status is not (TripStatus.Scheduled or TripStatus.Boarding) || trip.DepartureDate < GhanaClock.Today(clock))
                return Problems.Conflict("This trip is no longer open for booking.");
            if (seats.Any(s => s < 1 || s > trip.TotalSeats))
                return new Validator().Add("seatNumbers", $"seats must be between 1 and {trip.TotalSeats}.").Problem();

            await Expiry.PurgeAsync(db, clock);
            var session = req.SessionId!.Trim();
            var expires = clock.GetUtcNow().AddMinutes(opt.Value.SeatHoldMinutes);
            await using var tx = await db.Database.BeginTransactionAsync();
            // A session holds exactly the seats it last asked for on this trip.
            await db.SeatReservations.Where(r => r.TripId == id && r.HoldSessionId == session && r.BookingId == null).ExecuteDeleteAsync();
            var taken = await db.SeatReservations.Where(r => r.TripId == id && seats.Contains(r.SeatNumber)).Select(r => r.SeatNumber).ToListAsync();
            if (taken.Count > 0) return Problems.Conflict("Some seats are already taken.", new { unavailableSeats = taken.Order() });
            db.SeatReservations.AddRange(seats.Select(s => new SeatReservation { TripId = id, SeatNumber = s, HoldSessionId = session, ExpiresAt = expires }));
            try
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
            {
                return Problems.Conflict("Some seats were just taken by another traveller. Pick again.");
            }
            return Results.Ok(new { tripId = id, heldSeats = seats.Order(), sessionId = session, expiresAt = expires });
        }).RequireRateLimiting("public-write");
    }

    private static IQueryable<Trip> Load(AppDbContext db) =>
        db.Trips.Include(t => t.OriginTerminal).Include(t => t.DestinationTerminal).Include(t => t.Operator);

    private static async Task CheckRefs(Validator v, AppDbContext db, string? origin, string? dest, string? op)
    {
        if (origin is not null && !await db.Terminals.AnyAsync(t => t.Id == origin && t.IsActive)) v.Add("originStationId", "unknown or inactive terminal.");
        if (dest is not null && !await db.Terminals.AnyAsync(t => t.Id == dest && t.IsActive)) v.Add("destinationStationId", "unknown or inactive terminal.");
        if (origin is not null && origin == dest) v.Add("destinationStationId", "must differ from the origin terminal.");
        if (op is not null && !await db.TransportOperators.AnyAsync(o => o.Id == op && o.IsActive)) v.Add("operatorId", "unknown or inactive transport operator.");
    }

    private static List<string> Clean(List<string>? items) =>
        items?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Where(s => s.Length <= 120).Distinct().Take(30).ToList() ?? [];

    public static object Station(Terminal t) => new { id = t.Id, name = t.Name, city = t.City, address = t.Address };

    public static object Present(Trip t, int takenSeats) => new
    {
        id = t.Id,
        originCity = t.OriginCity,
        destinationCity = t.DestinationCity,
        originStation = Station(t.OriginTerminal),
        destinationStation = Station(t.DestinationTerminal),
        departureDate = Formats.Date(t.DepartureDate),
        departureTime = Formats.Time(t.DepartureTime),
        duration = Formats.Duration(t.DurationMinutes),
        basePrice = Money.ToCedis(t.BasePricePesewas),
        @operator = new { id = t.Operator.Id, name = t.Operator.Name, rating = t.Operator.Rating, isVerified = t.Operator.IsVerified },
        vehicleType = t.VehicleType,
        totalSeatsCount = t.TotalSeats,
        availableSeatsCount = Math.Max(0, t.TotalSeats - takenSeats),
        amenities = t.Amenities,
        status = t.Status.ToString().ToLowerInvariant(),
    };
}
