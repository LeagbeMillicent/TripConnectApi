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

public static class GroupsEndpoints
{
    public sealed record Coordinates(double? Lat, double? Lng);
    public sealed record Organizer(string? Name, string? Phone, string? Email);

    public sealed record CreateGroup(
        string? Name, string? TripType, string? Origin, string? Destination, string? PickupLocation,
        Coordinates? PickupCoordinates, string? TravelDate, string? PreferredDepartureTime, int? TargetTravelers, Organizer? Organizer);

    public sealed record FareApproval(decimal? ApprovedFarePerPerson, string? AssignedCoach, string? DispatchNotes, string? Status);

    public sealed record JoinGroup(string? Name, string? Phone, string? EmergencyContact, string? PaymentMethod,
        string? MobileMoneyProvider, string? Email);

    private static readonly IReadOnlyDictionary<string, GroupStatus> Statuses = new Dictionary<string, GroupStatus>(StringComparer.OrdinalIgnoreCase)
    {
        ["pending_approval"] = GroupStatus.PendingApproval, ["planning"] = GroupStatus.Planning,
        ["confirmed"] = GroupStatus.Confirmed, ["cancelled"] = GroupStatus.Cancelled,
    };

    private static string Wire(GroupStatus s) => Statuses.First(kv => kv.Value == s).Key;

    public static void MapGroups(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/groups").WithTags("Group Charters");

        g.MapPost("/", async (CreateGroup req, AppDbContext db, TimeProvider clock, INotifier notifier) =>
        {
            var v = new Validator().Require("name", req.Name, 150).Require("tripType", req.TripType, 60)
                .Require("origin", req.Origin, 150).Require("destination", req.Destination, 150)
                .Require("pickupLocation", req.PickupLocation, 250)
                .Require("organizer.name", req.Organizer?.Name, 120).Optional("organizer.email", req.Organizer?.Email, 254);
            var phone = v.Phone("organizer.phone", req.Organizer?.Phone);
            var date = v.Date("travelDate", req.TravelDate);
            var time = v.Time("preferredDepartureTime", req.PreferredDepartureTime);
            if (v.IsValid && date < GhanaClock.Today(clock)) v.Add("travelDate", "cannot be in the past.");
            if (req.TargetTravelers is null or < 2 or > 200) v.Add("targetTravelers", "must be between 2 and 200.");
            var c = req.PickupCoordinates;
            if (c is not null && (c.Lat is null or < -90 or > 90 || c.Lng is null or < -180 or > 180))
                v.Add("pickupCoordinates", "lat must be -90..90 and lng -180..180.");
            if (!v.IsValid) return v.Problem();

            var group = new GroupCharter
            {
                InviteCode = "", Name = req.Name!.Trim(), TripType = req.TripType!.Trim(), Origin = req.Origin!.Trim(),
                Destination = req.Destination!.Trim(), PickupLocation = req.PickupLocation!.Trim(),
                PickupLatitude = c?.Lat, PickupLongitude = c?.Lng, TravelDate = date, PreferredDepartureTime = time,
                TargetTravelers = req.TargetTravelers!.Value, OrganizerName = req.Organizer!.Name!.Trim(),
                OrganizerPhone = phone, OrganizerEmail = req.Organizer.Email?.Trim(),
            };
            db.Groups.Add(group);
            for (var attempt = 0; ; attempt++)
            {
                group.InviteCode = $"{Codes.CityPrefix(group.Origin)}-{Codes.CityPrefix(group.Destination)}-{Codes.Alnum(4)}";
                try { await db.SaveChangesAsync(); break; }
                catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex) && attempt < 5) { }
            }
            notifier.Enqueue(new Notification(phone, group.OrganizerEmail, $"RiconRide charter {group.InviteCode} received",
                $"RiconRide: we received your charter request \"{group.Name}\". Invite code {group.InviteCode}. " +
                "Our dispatch team will confirm the fare per person shortly."));
            return Results.Created($"/api/groups/code/{group.InviteCode}", Public(group, 0));
        }).RequireRateLimiting("public-write");

        g.MapPatch("/{id:guid}/fare-approval", async (Guid id, FareApproval req, ClaimsPrincipal user, AppDbContext db, TimeProvider clock, INotifier notifier) =>
        {
            var group = await db.Groups.FindAsync(id);
            if (group is null) return Problems.NotFound("Group charter");
            var v = new Validator().Optional("assignedCoach", req.AssignedCoach, 150).Optional("dispatchNotes", req.DispatchNotes, 1000);
            var status = v.OneOf("status", req.Status, Statuses, required: false, fallback: GroupStatus.Planning);
            if (status == GroupStatus.PendingApproval) v.Add("status", "cannot be set back to pending_approval.");
            var fare = status == GroupStatus.Cancelled ? group.ApprovedFarePesewas : v.Amount("approvedFarePerPerson", req.ApprovedFarePerPerson);
            if (!v.IsValid) return v.Problem();

            await Expiry.PurgeAsync(db, clock);
            var members = await db.GroupMembers.Occupying(id, clock.GetUtcNow()).CountAsync();
            // Once anyone has paid or is paying, the fare they were quoted is locked.
            if (members > 0 && fare != group.ApprovedFarePesewas)
                return Problems.Conflict("Fare cannot change after travellers have joined.");
            if (group.Status == GroupStatus.Cancelled) return Problems.Conflict("Charter is cancelled.");

            var firstApproval = group.ApprovedFarePesewas is null && status != GroupStatus.Cancelled;
            group.ApprovedFarePesewas = fare;
            group.AssignedCoach = req.AssignedCoach?.Trim() ?? group.AssignedCoach;
            group.DispatchNotes = req.DispatchNotes?.Trim() ?? group.DispatchNotes;
            group.Status = status;
            group.ApprovedBy = user.OperatorId();
            group.ApprovedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            if (firstApproval)
                notifier.Enqueue(new Notification(group.OrganizerPhone, group.OrganizerEmail, $"RiconRide charter {group.InviteCode} approved",
                    $"RiconRide: \"{group.Name}\" is approved at {Money.Format(fare!.Value)} per person. Share code {group.InviteCode} so travellers can join and pay."));
            var paid = await db.GroupMembers.CountAsync(m => m.GroupId == id && m.Status == MemberStatus.Paid);
            return Results.Ok(Admin(group, paid));
        }).RequireRoles(OperatorRole.DispatchMarshal);

        g.MapGet("/code/{code}", async (string code, AppDbContext db, TimeProvider clock) =>
        {
            await Expiry.PurgeAsync(db, clock);
            var group = await db.Groups.SingleOrDefaultAsync(x => x.InviteCode == code.Trim().ToUpperInvariant());
            if (group is null) return Problems.NotFound("Group charter");
            var paid = await db.GroupMembers.CountAsync(m => m.GroupId == group.Id && m.Status == MemberStatus.Paid);
            var occupying = await db.GroupMembers.Occupying(group.Id, clock.GetUtcNow()).CountAsync();
            var dto = Public(group, paid);
            return Results.Ok(new Dictionary<string, object?>(dto) { ["seatsRemaining"] = Math.Max(0, group.TargetTravelers - occupying) });
        });

        g.MapPost("/{id:guid}/join", async (Guid id, JoinGroup req, AppDbContext db, PaymentService payments,
            TimeProvider clock, IOptions<PlatformOptions> opt, CancellationToken ct) =>
        {
            var v = new Validator().Require("name", req.Name, 120).Optional("email", req.Email, 254);
            var phone = v.Phone("phone", req.Phone);
            var emergency = v.Phone("emergencyContact", req.EmergencyContact, required: false);
            var method = v.Choice("paymentMethod", req.PaymentMethod, Choices.PaymentMethods);
            var network = method == "momo" ? v.OptionalChoice("mobileMoneyProvider", req.MobileMoneyProvider, Choices.MomoProviders) : null;
            if (!v.IsValid) return v.Problem();

            await Expiry.PurgeAsync(db, clock, ct);
            var now = clock.GetUtcNow();
            GroupMember member;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                // Row lock serialises joins so the last place cannot be sold twice.
                var group = await db.Groups.FromSql($"SELECT * FROM group_charters WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
                if (group is null) return Problems.NotFound("Group charter");
                if (group.Status is not (GroupStatus.Planning or GroupStatus.Confirmed) || group.ApprovedFarePesewas is null)
                    return Problems.Conflict(group.Status == GroupStatus.PendingApproval
                        ? "This charter's fare has not been approved yet." : "This charter is not open for joining.");
                if (group.TravelDate < GhanaClock.Today(clock)) return Problems.Conflict("This charter has already travelled.");
                var occupying = await db.GroupMembers.Occupying(id, now).ToListAsync(ct);
                if (occupying.Any(m => m.Phone == phone)) return Problems.Conflict("This phone number has already joined the charter.");
                if (occupying.Count >= group.TargetTravelers) return Problems.Conflict("This charter is full.");

                member = new GroupMember
                {
                    GroupId = id, Name = req.Name!.Trim(), Phone = phone, EmergencyContact = emergency == "" ? null : emergency,
                    PaymentMethod = method, FarePesewas = group.ApprovedFarePesewas.Value,
                    ExpiresAt = now.AddMinutes(opt.Value.PaymentWindowMinutes),
                };
                db.GroupMembers.Add(member);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            var start = await payments.StartAsync(PaymentKind.Charter, member.Id, member.FarePesewas, phone, req.Email?.Trim(), network, ct);
            var body = new Dictionary<string, object?>
            {
                ["memberId"] = member.Id, ["status"] = "pending_payment", ["fare"] = Money.ToCedis(member.FarePesewas),
                ["paymentExpiresAt"] = member.ExpiresAt, ["payment"] = PaymentsEndpoints.Present(start.Payment),
            };
            if (start.Error is not null) body["paymentError"] = start.Error;
            return Results.Created($"/api/groups/{id}/members/{member.Id}", body);
        }).RequireRateLimiting("public-write");
    }

    /// <summary>Public view: organiser shown by first name only; no member names or phones.</summary>
    private static IDictionary<string, object?> Public(GroupCharter g, int confirmedMembers) => new Dictionary<string, object?>
    {
        ["id"] = g.Id,
        ["inviteCode"] = g.InviteCode,
        ["name"] = g.Name,
        ["tripType"] = g.TripType,
        ["origin"] = g.Origin,
        ["destination"] = g.Destination,
        ["pickupLocation"] = g.PickupLocation,
        ["pickupCoordinates"] = g.PickupLatitude is null ? null : new { lat = g.PickupLatitude, lng = g.PickupLongitude },
        ["pickupMapUrl"] = Formats.MapsDirections(g.PickupLatitude, g.PickupLongitude, g.PickupLocation),
        ["travelDate"] = Formats.Date(g.TravelDate),
        ["preferredDepartureTime"] = Formats.Time(g.PreferredDepartureTime),
        ["targetTravelers"] = g.TargetTravelers,
        ["confirmedMembers"] = confirmedMembers,
        ["approvedFarePerPerson"] = g.ApprovedFarePesewas is null ? null : Money.ToCedis(g.ApprovedFarePesewas.Value),
        ["assignedCoach"] = g.AssignedCoach,
        ["organizerFirstName"] = g.OrganizerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
        ["status"] = Wire(g.Status),
    };

    private static IDictionary<string, object?> Admin(GroupCharter g, int paid) => new Dictionary<string, object?>(Public(g, paid))
    {
        ["organizer"] = new { name = g.OrganizerName, phone = GhanaPhone.ToLocal(g.OrganizerPhone), email = g.OrganizerEmail },
        ["dispatchNotes"] = g.DispatchNotes,
        ["approvedAt"] = g.ApprovedAt,
    };
}
