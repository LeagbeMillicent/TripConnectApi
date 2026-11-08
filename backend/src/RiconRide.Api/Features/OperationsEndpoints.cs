using Microsoft.EntityFrameworkCore;
using RiconRide.Api.Auth;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;
using RiconRide.Api.Services;

namespace RiconRide.Api.Features;

/// <summary>Terminals, broadcasts/FAQs and the executive dashboard.</summary>
public static class OperationsEndpoints
{
    public sealed record TerminalWrite(
        string? Id, string? Name, string? City, string? Address, double? Latitude, double? Longitude,
        int? ActiveBayCount, string? MarshalName, string? MarshalPhone, bool? IsActive);

    public sealed record BannerWrite(string? Title, string? Message, string? Severity, string? LinkUrl, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool? IsActive);

    public sealed record FaqWrite(string? Question, string? Answer, string? Category, int? SortOrder, bool? IsPublished);

    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        var t = app.MapGroup("/api/terminals").WithTags("Terminals");

        t.MapGet("/", async (string? city, AppDbContext db) =>
        {
            var q = db.Terminals.Where(x => x.IsActive);
            if (!string.IsNullOrWhiteSpace(city)) q = q.Where(x => EF.Functions.ILike(x.City, city.Trim()));
            var list = await q.OrderBy(x => x.City).ThenBy(x => x.Name).ToListAsync();
            return Results.Ok(list.Select(Present));
        });

        t.MapPost("/", async (TerminalWrite req, AppDbContext db) =>
        {
            var v = Validate(req, creating: true);
            var marshal = v.Phone("marshalPhone", req.MarshalPhone, required: false);
            if (!v.IsValid) return v.Problem();
            var id = string.IsNullOrWhiteSpace(req.Id) ? "st-" + Codes.Slug(req.Name!) : Codes.Slug(req.Id);
            var terminal = new Terminal
            {
                Id = id, Name = req.Name!.Trim(), City = req.City!.Trim(), Address = req.Address!.Trim(),
                Latitude = req.Latitude, Longitude = req.Longitude, ActiveBayCount = req.ActiveBayCount ?? 0,
                MarshalName = req.MarshalName?.Trim(), MarshalPhone = marshal == "" ? null : marshal, IsActive = req.IsActive ?? true,
            };
            db.Terminals.Add(terminal);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex)) { return Problems.Conflict($"Terminal id '{id}' already exists."); }
            return Results.Created($"/api/terminals/{id}", Present(terminal));
        }).RequireRoles(OperatorRole.NetworkManager, OperatorRole.StationMaster);

        t.MapPatch("/{id}", async (string id, TerminalWrite req, AppDbContext db) =>
        {
            var terminal = await db.Terminals.FindAsync(id);
            if (terminal is null) return Problems.NotFound("Terminal");
            var v = Validate(req, creating: false);
            var marshal = v.Phone("marshalPhone", req.MarshalPhone, required: false);
            if (!v.IsValid) return v.Problem();
            if (req.Name is not null) terminal.Name = req.Name.Trim();
            if (req.City is not null) terminal.City = req.City.Trim();
            if (req.Address is not null) terminal.Address = req.Address.Trim();
            if (req.Latitude is not null) terminal.Latitude = req.Latitude;
            if (req.Longitude is not null) terminal.Longitude = req.Longitude;
            if (req.ActiveBayCount is not null) terminal.ActiveBayCount = req.ActiveBayCount.Value;
            if (req.MarshalName is not null) terminal.MarshalName = req.MarshalName.Trim();
            if (req.MarshalPhone is not null) terminal.MarshalPhone = marshal == "" ? null : marshal;
            if (req.IsActive is not null) terminal.IsActive = req.IsActive.Value;
            await db.SaveChangesAsync();
            return Results.Ok(Present(terminal));
        }).RequireRoles(OperatorRole.NetworkManager, OperatorRole.StationMaster);

        var c = app.MapGroup("/api/content").WithTags("Content");

        c.MapGet("/banners", async (AppDbContext db, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var list = await db.Banners.Where(b => b.IsActive && (b.StartsAt == null || b.StartsAt <= now) && (b.EndsAt == null || b.EndsAt > now))
                .OrderByDescending(b => b.CreatedAt).ToListAsync();
            return Results.Ok(list.Select(PresentBanner));
        });

        c.MapPost("/banners", async (BannerWrite req, AppDbContext db) =>
        {
            var v = new Validator().Require("title", req.Title, 120).Require("message", req.Message, 1000).Optional("linkUrl", req.LinkUrl, 500);
            var severity = v.OneOf("severity", req.Severity, Choices.BannerSeverities, required: false, fallback: "info");
            if (req.LinkUrl is not null && !Uri.TryCreate(req.LinkUrl, UriKind.Absolute, out _)) v.Add("linkUrl", "must be an absolute URL.");
            if (req.StartsAt is not null && req.EndsAt is not null && req.EndsAt <= req.StartsAt) v.Add("endsAt", "must be after startsAt.");
            if (!v.IsValid) return v.Problem();
            var banner = new Banner
            {
                Title = req.Title!.Trim(), Message = req.Message!.Trim(), Severity = severity, LinkUrl = req.LinkUrl,
                StartsAt = req.StartsAt, EndsAt = req.EndsAt, IsActive = req.IsActive ?? true,
            };
            db.Banners.Add(banner);
            await db.SaveChangesAsync();
            return Results.Created($"/api/content/banners/{banner.Id}", PresentBanner(banner));
        }).RequireRoles(OperatorRole.Communications);

        c.MapGet("/faqs", async (string? category, AppDbContext db) =>
        {
            var q = db.Faqs.Where(f => f.IsPublished);
            if (!string.IsNullOrWhiteSpace(category)) q = q.Where(f => f.Category == category);
            var list = await q.OrderBy(f => f.SortOrder).ThenBy(f => f.CreatedAt).ToListAsync();
            return Results.Ok(list.Select(PresentFaq));
        });

        c.MapPost("/faqs", async (FaqWrite req, AppDbContext db) =>
        {
            var v = new Validator().Require("question", req.Question, 300).Require("answer", req.Answer, 4000).Optional("category", req.Category, 60);
            if (!v.IsValid) return v.Problem();
            var faq = new Faq
            {
                Question = req.Question!.Trim(), Answer = req.Answer!.Trim(), Category = req.Category?.Trim(),
                SortOrder = req.SortOrder ?? 0, IsPublished = req.IsPublished ?? true,
            };
            db.Faqs.Add(faq);
            await db.SaveChangesAsync();
            return Results.Created($"/api/content/faqs/{faq.Id}", PresentFaq(faq));
        }).RequireRoles(OperatorRole.Communications);

        app.MapGet("/api/admin/dashboard-stats", async (AppDbContext db, TimeProvider clock) =>
        {
            await Expiry.PurgeAsync(db, clock);
            var now = clock.GetUtcNow();
            var today = GhanaClock.Today(clock);
            var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var paid = db.Payments.Where(p => p.Status == PaymentStatus.Success);
            // Every figure is a count or sum of real rows; nothing is estimated.
            var gross = await paid.SumAsync(p => (long?)p.AmountPesewas) ?? 0;
            var grossToday = await paid.Where(p => p.PaidAt >= dayStart).SumAsync(p => (long?)p.AmountPesewas) ?? 0;
            var seatsToday = await db.BookingPassengers.CountAsync(p => db.Bookings.Any(b => b.Id == p.BookingId
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Boarded) && b.ConfirmedAt >= dayStart));
            var tripsInTransit = await db.Trips.CountAsync(t => t.Status == TripStatus.Departed);
            var rentalsOut = await db.Rentals.CountAsync(r => r.Status == RentalStatus.Confirmed && r.StartDate <= today && r.EndDate >= today);
            var pendingFares = await db.Groups.CountAsync(g => g.Status == GroupStatus.PendingApproval);
            var refundsDue = await db.Payments.CountAsync(p => p.Status == PaymentStatus.RefundDue);
            return Results.Ok(new
            {
                asOf = now,
                grossVolume = Money.ToCedis(gross),
                grossVolumeToday = Money.ToCedis(grossToday),
                seatsBookedToday = seatsToday,
                fleetInTransit = new { trips = tripsInTransit, rentalVehicles = rentalsOut, total = tripsInTransit + rentalsOut },
                pendingFareApprovals = pendingFares,
                paymentsRefundDue = refundsDue,
            });
        }).WithTags("Admin").RequireRoles(OperatorRole.Executive);
    }

    private static Validator Validate(TerminalWrite req, bool creating)
    {
        var v = new Validator().Optional("marshalName", req.MarshalName, 120).Optional("id", req.Id, 60);
        if (creating) v.Require("name", req.Name, 150).Require("city", req.City, 80).Require("address", req.Address, 250);
        else v.Optional("name", req.Name, 150).Optional("city", req.City, 80).Optional("address", req.Address, 250);
        if (req.Latitude is < -90 or > 90) v.Add("latitude", "must be between -90 and 90.");
        if (req.Longitude is < -180 or > 180) v.Add("longitude", "must be between -180 and 180.");
        if ((req.Latitude is null) != (req.Longitude is null)) v.Add("longitude", "latitude and longitude must be given together.");
        if (req.ActiveBayCount is < 0 or > 500) v.Add("activeBayCount", "must be between 0 and 500.");
        return v;
    }

    private static object Present(Terminal t) => new
    {
        id = t.Id, name = t.Name, city = t.City, address = t.Address,
        coordinates = t.Latitude is null ? null : new { lat = t.Latitude, lng = t.Longitude },
        directionsUrl = Formats.MapsDirections(t.Latitude, t.Longitude, $"{t.Name}, {t.Address}, {t.City}, Ghana"),
        activeBayCount = t.ActiveBayCount,
        marshal = t.MarshalName is null && t.MarshalPhone is null ? null
            : new { name = t.MarshalName, phone = t.MarshalPhone is null ? null : GhanaPhone.ToLocal(t.MarshalPhone) },
        isActive = t.IsActive,
    };

    private static object PresentBanner(Banner b) => new
    {
        id = b.Id, title = b.Title, message = b.Message, severity = b.Severity, linkUrl = b.LinkUrl, startsAt = b.StartsAt, endsAt = b.EndsAt, isActive = b.IsActive,
    };

    private static object PresentFaq(Faq f) => new
    {
        id = f.Id, question = f.Question, answer = f.Answer, category = f.Category, sortOrder = f.SortOrder, isPublished = f.IsPublished,
    };
}
