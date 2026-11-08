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

public static class FleetEndpoints
{
    public sealed record VehicleWrite(
        string? Name, string? Category, string? LicensePlate, decimal? DailyPrice, decimal? WeeklyPrice,
        decimal? SecurityDeposit, bool? WithDriverAllowed, bool? SelfDriveAllowed, decimal? DriverFeePerDay,
        List<string>? Features, string? DvlaRoadworthinessExpiry, List<string>? Images);

    public sealed record Customer(string? Name, string? Phone, string? Email);

    public sealed record CreateRental(
        Guid? VehicleId, string? StartDate, string? EndDate, string? PickupPoint, string? DriverOption,
        Customer? Customer, string? DriverLicenseNumber, string? PaymentMethod, string? MobileMoneyProvider);

    public sealed record Deduction(string? Reason, decimal? Amount);
    public sealed record DepositSettlement(string? Action, List<Deduction>? Deductions, string? InspectionNotes);

    private static readonly IReadOnlyDictionary<string, string> DriverOptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["self-drive"] = "self-drive", ["with-driver"] = "with-driver" };
    private static readonly IReadOnlyDictionary<string, string> DepositActions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["release"] = "release", ["deduct"] = "deduct", ["forfeit"] = "forfeit" };

    private const int MaxRentalDays = 90;

    public static void MapFleet(this IEndpointRouteBuilder app)
    {
        var vehicles = app.MapGroup("/api/vehicles").WithTags("Vehicles");

        vehicles.MapGet("/", async (string? category, string? driverOption, string? availability, string? startDate, string? endDate,
            AppDbContext db, TimeProvider clock) =>
        {
            var v = new Validator();
            var cat = v.OptionalChoice("category", category, Choices.VehicleCategories);
            var drv = v.OptionalChoice("driverOption", driverOption, DriverOptions);
            var today = GhanaClock.Today(clock);
            var from = startDate is null ? today : v.Date("startDate", startDate);
            var to = endDate is null ? from : v.Date("endDate", endDate);
            if (availability is not null && availability != "available") v.Add("availability", "only 'available' is supported.");
            if (!v.IsValid) return v.Problem();

            await Expiry.PurgeAsync(db, clock);
            var now = clock.GetUtcNow();
            // Unroadworthy vehicles are never offered: renting one out is illegal.
            var q = db.Vehicles.Where(x => x.IsActive && x.DvlaRoadworthinessExpiry >= to);
            if (cat is not null) q = q.Where(x => x.Category == cat);
            if (drv == "self-drive") q = q.Where(x => x.SelfDriveAllowed);
            if (drv == "with-driver") q = q.Where(x => x.WithDriverAllowed);
            if (availability == "available")
                q = q.Where(x => !db.Rentals.Any(r => r.VehicleId == x.Id && r.StartDate <= to && r.EndDate >= from
                    && (r.Status == RentalStatus.Confirmed || (r.Status == RentalStatus.PendingPayment && r.ExpiresAt >= now))));
            var list = await q.OrderBy(x => x.Category).ThenBy(x => x.DailyPesewas).ToListAsync();
            return Results.Ok(list.Select(Present));
        });

        vehicles.MapPost("/", async (VehicleWrite req, AppDbContext db, TimeProvider clock) =>
        {
            var v = new Validator().Require("name", req.Name, 120).Require("licensePlate", req.LicensePlate, 20);
            var cat = v.Choice("category", req.Category, Choices.VehicleCategories);
            var daily = v.Amount("dailyPrice", req.DailyPrice);
            long? weekly = req.WeeklyPrice is null ? null : v.Amount("weeklyPrice", req.WeeklyPrice);
            var deposit = v.Amount("securityDeposit", req.SecurityDeposit, allowZero: true);
            var withDriver = req.WithDriverAllowed ?? false;
            var selfDrive = req.SelfDriveAllowed ?? false;
            var driverFee = withDriver ? v.Amount("driverFeePerDay", req.DriverFeePerDay ?? 0, allowZero: true) : 0;
            if (!withDriver && !selfDrive) v.Add("selfDriveAllowed", "at least one of withDriverAllowed / selfDriveAllowed must be true.");
            var dvla = v.Date("dvlaRoadworthinessExpiry", req.DvlaRoadworthinessExpiry);
            if (v.IsValid && dvla < GhanaClock.Today(clock)) v.Add("dvlaRoadworthinessExpiry", "has already expired; the vehicle cannot be listed.");
            var images = req.Images ?? [];
            if (images.Any(i => !Uri.TryCreate(i, UriKind.Absolute, out var u) || u.Scheme != "https"))
                v.Add("images", "must be https URLs (Cloudinary/S3).");
            if (!v.IsValid) return v.Problem();

            var plate = req.LicensePlate!.Trim().ToUpperInvariant();
            var vehicle = new Vehicle
            {
                Name = req.Name!.Trim(), Category = cat, LicensePlate = plate, DailyPesewas = daily, WeeklyPesewas = weekly,
                DepositPesewas = deposit, WithDriverAllowed = withDriver, SelfDriveAllowed = selfDrive,
                DriverFeePerDayPesewas = driverFee, DvlaRoadworthinessExpiry = dvla,
                Features = req.Features?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Take(30).ToList() ?? [],
                Images = images.Take(20).ToList(),
            };
            db.Vehicles.Add(vehicle);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
            {
                return Problems.Conflict($"A vehicle with plate {plate} is already registered.");
            }
            return Results.Created($"/api/vehicles/{vehicle.Id}", Present(vehicle));
        }).RequireRoles(OperatorRole.FleetManager);

        var rentals = app.MapGroup("/api/rentals").WithTags("Rentals");

        rentals.MapPost("/", async (CreateRental req, AppDbContext db, PaymentService payments, TimeProvider clock,
            IOptions<PlatformOptions> opt, CancellationToken ct) =>
        {
            var v = new Validator().Require("pickupPoint", req.PickupPoint, 250)
                .Require("customer.name", req.Customer?.Name, 120).Optional("customer.email", req.Customer?.Email, 254)
                .Optional("driverLicenseNumber", req.DriverLicenseNumber, 40);
            if (req.VehicleId is null) v.Add("vehicleId", "is required.");
            var phone = v.Phone("customer.phone", req.Customer?.Phone);
            var start = v.Date("startDate", req.StartDate);
            var end = v.Date("endDate", req.EndDate);
            var drv = v.Choice("driverOption", req.DriverOption, DriverOptions);
            var method = v.Choice("paymentMethod", req.PaymentMethod ?? "momo", Choices.PaymentMethods);
            var network = method == "momo" ? v.OptionalChoice("mobileMoneyProvider", req.MobileMoneyProvider, Choices.MomoProviders) : null;
            if (v.IsValid)
            {
                if (start < GhanaClock.Today(clock)) v.Add("startDate", "cannot be in the past.");
                if (end < start) v.Add("endDate", "must be on or after startDate.");
                else if (end.DayNumber - start.DayNumber + 1 > MaxRentalDays) v.Add("endDate", $"rentals are limited to {MaxRentalDays} days.");
            }
            if (drv == "self-drive" && string.IsNullOrWhiteSpace(req.DriverLicenseNumber))
                v.Add("driverLicenseNumber", "is required for self-drive rentals.");
            if (!v.IsValid) return v.Problem();

            await Expiry.PurgeAsync(db, clock, ct);
            var now = clock.GetUtcNow();
            Rental rental;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                // Lock the vehicle row so two customers cannot book overlapping dates at once.
                var vehicle = await db.Vehicles.FromSql($"SELECT * FROM vehicles WHERE id = {req.VehicleId} FOR UPDATE").SingleOrDefaultAsync(ct);
                if (vehicle is null || !vehicle.IsActive) return Problems.NotFound("Vehicle");
                var withDriver = drv == "with-driver";
                if (withDriver && !vehicle.WithDriverAllowed) return Problems.Unprocessable("This vehicle is not offered with a driver.");
                if (!withDriver && !vehicle.SelfDriveAllowed) return Problems.Unprocessable("This vehicle is not offered for self-drive.");
                if (vehicle.DvlaRoadworthinessExpiry < end) return Problems.Unprocessable("Vehicle's DVLA roadworthiness expires before the rental ends.");
                if (await db.Rentals.Blocking(vehicle.Id, start, end, now).AnyAsync(ct))
                    return Problems.Conflict("Vehicle is already booked for some of those dates.");

                var days = end.DayNumber - start.DayNumber + 1;
                var rent = Quote(vehicle, days);
                var driverFee = withDriver ? vehicle.DriverFeePerDayPesewas * days : 0;
                rental = new Rental
                {
                    RentalNumber = Codes.RentalNumber(now), VehicleId = vehicle.Id, StartDate = start, EndDate = end,
                    PickupPoint = req.PickupPoint!.Trim(), WithDriver = withDriver, CustomerName = req.Customer!.Name!.Trim(),
                    CustomerPhone = phone, CustomerEmail = req.Customer.Email?.Trim(), DriverLicenseNumber = req.DriverLicenseNumber?.Trim(),
                    RentalPesewas = rent, DriverFeePesewas = driverFee, DepositPesewas = vehicle.DepositPesewas,
                    TotalPesewas = rent + driverFee + vehicle.DepositPesewas, ExpiresAt = now.AddMinutes(opt.Value.PaymentWindowMinutes),
                };
                db.Rentals.Add(rental);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            var pay = await payments.StartAsync(PaymentKind.Rental, rental.Id, rental.TotalPesewas, phone, rental.CustomerEmail, network, ct);
            var body = PresentRental(rental);
            body["payment"] = PaymentsEndpoints.Present(pay.Payment);
            body["paymentExpiresAt"] = rental.ExpiresAt;
            if (pay.Error is not null) body["paymentError"] = pay.Error;
            return Results.Created($"/api/rentals/{rental.Id}", body);
        }).RequireRateLimiting("public-write");

        rentals.MapPatch("/{id:guid}/deposit", async (Guid id, DepositSettlement req, ClaimsPrincipal user, AppDbContext db,
            IPaymentGateway gateway, INotifier notifier, TimeProvider clock, CancellationToken ct) =>
        {
            var v = new Validator().Optional("inspectionNotes", req.InspectionNotes, 2000);
            var action = v.Choice("action", req.Action, DepositActions);
            var deductions = (req.Deductions ?? []).Select((d, i) =>
            {
                v.Require($"deductions[{i}].reason", d.Reason, 200);
                return new RentalDeduction { Reason = d.Reason?.Trim() ?? "", AmountPesewas = v.Amount($"deductions[{i}].amount", d.Amount) };
            }).ToList();
            if (action == "deduct" && deductions.Count == 0) v.Add("deductions", "are required when action is 'deduct'.");
            if (action != "deduct" && deductions.Count > 0) v.Add("deductions", "are only allowed when action is 'deduct'.");
            if (!v.IsValid) return v.Problem();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Locked for the whole settlement, including the refund call, so a
            // double-clicked "release" can never refund the deposit twice.
            var rental = await db.Rentals.FromSql($"SELECT * FROM rentals WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (rental is null) return Problems.NotFound("Rental");
            if (rental.Status != RentalStatus.Confirmed || rental.DepositStatus != DepositStatus.Held)
                return Problems.Conflict("Deposit is not currently held for this rental.");
            if (GhanaClock.Today(clock) < rental.StartDate) return Problems.Conflict("The rental has not started; cancel it instead.");

            var deducted = deductions.Sum(d => d.AmountPesewas);
            if (deducted > rental.DepositPesewas)
                return new Validator().Add("deductions", $"total {Money.Format(deducted)} exceeds the {Money.Format(rental.DepositPesewas)} deposit.").Problem();
            var refund = action switch { "release" => rental.DepositPesewas, "deduct" => rental.DepositPesewas - deducted, _ => 0 };

            if (refund > 0)
            {
                var paid = await db.Payments.Where(p => p.Kind == PaymentKind.Rental && p.SubjectId == id && p.Status == PaymentStatus.Success)
                    .Select(p => p.Reference).FirstOrDefaultAsync(ct);
                if (paid is null) return Problems.Conflict("No settled payment found to refund against.");
                var result = await gateway.RefundAsync(paid, refund, ct);
                if (!result.Ok) return Problems.BadGateway("Refund failed: " + result.Error);
            }

            rental.Deductions.AddRange(deductions);
            rental.DepositRefundPesewas = refund;
            rental.DepositStatus = refund == rental.DepositPesewas ? DepositStatus.Released : refund == 0 ? DepositStatus.Forfeited : DepositStatus.PartiallyReleased;
            rental.Status = RentalStatus.Completed;
            rental.InspectionNotes = req.InspectionNotes?.Trim();
            rental.DepositSettledAt = clock.GetUtcNow();
            rental.DepositSettledBy = user.OperatorId();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            notifier.Enqueue(new Notification(rental.CustomerPhone, rental.CustomerEmail, $"RiconRide deposit {rental.RentalNumber}",
                refund > 0
                    ? $"RiconRide: vehicle returned. {Money.Format(refund)} of your {Money.Format(rental.DepositPesewas)} deposit is being refunded to your original payment method." +
                      (deducted > 0 ? $" Deductions: {string.Join("; ", deductions.Select(d => $"{d.Reason} {Money.Format(d.AmountPesewas)}"))}." : "")
                    : $"RiconRide: vehicle returned. Your {Money.Format(rental.DepositPesewas)} deposit was retained. Call us for details."));
            var body = PresentRental(rental);
            body["deductions"] = deductions.Select(d => new { reason = d.Reason, amount = Money.ToCedis(d.AmountPesewas) });
            return Results.Ok(body);
        }).RequireRoles(OperatorRole.FleetManager);
    }

    /// <summary>Full weeks at the weekly rate, leftover days at the daily rate, capped so they never cost more than a week.</summary>
    public static long Quote(Vehicle v, int days)
    {
        if (v.WeeklyPesewas is not { } weekly) return v.DailyPesewas * days;
        var rest = v.DailyPesewas * (days % 7);
        return days / 7 * weekly + Math.Min(rest, weekly);
    }

    private static object Present(Vehicle v) => new
    {
        id = v.Id, name = v.Name, category = v.Category, licensePlate = v.LicensePlate,
        dailyPrice = Money.ToCedis(v.DailyPesewas),
        weeklyPrice = v.WeeklyPesewas is null ? (decimal?)null : Money.ToCedis(v.WeeklyPesewas.Value),
        securityDeposit = Money.ToCedis(v.DepositPesewas),
        withDriverAllowed = v.WithDriverAllowed, selfDriveAllowed = v.SelfDriveAllowed,
        driverFeePerDay = Money.ToCedis(v.DriverFeePerDayPesewas),
        features = v.Features, dvlaRoadworthinessExpiry = Formats.Date(v.DvlaRoadworthinessExpiry), images = v.Images,
    };

    private static Dictionary<string, object?> PresentRental(Rental r) => new()
    {
        ["id"] = r.Id,
        ["rentalNumber"] = r.RentalNumber,
        ["vehicleId"] = r.VehicleId,
        ["startDate"] = Formats.Date(r.StartDate),
        ["endDate"] = Formats.Date(r.EndDate),
        ["days"] = r.EndDate.DayNumber - r.StartDate.DayNumber + 1,
        ["pickupPoint"] = r.PickupPoint,
        ["driverOption"] = r.WithDriver ? "with-driver" : "self-drive",
        ["rentalAmount"] = Money.ToCedis(r.RentalPesewas),
        ["driverFee"] = Money.ToCedis(r.DriverFeePesewas),
        ["securityDeposit"] = Money.ToCedis(r.DepositPesewas),
        ["totalAmount"] = Money.ToCedis(r.TotalPesewas),
        ["status"] = r.Status switch { RentalStatus.PendingPayment => "pending_payment", var s => s.ToString().ToLowerInvariant() },
        ["depositStatus"] = r.DepositStatus switch
        {
            DepositStatus.NotHeld => "not_held", DepositStatus.PartiallyReleased => "partially_released", var s => s.ToString().ToLowerInvariant(),
        },
        ["depositRefunded"] = r.DepositRefundPesewas is null ? null : Money.ToCedis(r.DepositRefundPesewas.Value),
    };
}
