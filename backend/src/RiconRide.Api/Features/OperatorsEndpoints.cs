using Microsoft.EntityFrameworkCore;
using RiconRide.Api.Auth;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;

namespace RiconRide.Api.Features;

/// <summary>
/// Transport companies (VIP, STC, ...) that trips are scheduled under.
/// Rating is deliberately not writable: it may only come from a real review
/// system, never be typed in by staff.
/// </summary>
public static class OperatorsEndpoints
{
    public sealed record OperatorWrite(string? Id, string? Name, bool? IsVerified, bool? IsActive);

    public static void MapTransportOperators(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/operators").WithTags("Transport Operators");

        g.MapGet("/", async (AppDbContext db) =>
            Results.Ok((await db.TransportOperators.Where(o => o.IsActive).OrderBy(o => o.Name).ToListAsync()).Select(Present)));

        g.MapPost("/", async (OperatorWrite req, AppDbContext db) =>
        {
            var v = new Validator().Require("name", req.Name, 120).Optional("id", req.Id, 60);
            if (!v.IsValid) return v.Problem();
            var id = string.IsNullOrWhiteSpace(req.Id) ? "op-" + Codes.Slug(req.Name!) : Codes.Slug(req.Id);
            var op = new TransportOperator { Id = id, Name = req.Name!.Trim(), IsVerified = req.IsVerified ?? false, IsActive = req.IsActive ?? true };
            db.TransportOperators.Add(op);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex)) { return Problems.Conflict($"Operator id '{id}' already exists."); }
            return Results.Created($"/api/operators/{id}", Present(op));
        }).RequireRoles(OperatorRole.NetworkManager);

        g.MapPatch("/{id}", async (string id, OperatorWrite req, AppDbContext db) =>
        {
            var op = await db.TransportOperators.FindAsync(id);
            if (op is null) return Problems.NotFound("Transport operator");
            var v = new Validator().Optional("name", req.Name, 120);
            if (req.Name is not null && string.IsNullOrWhiteSpace(req.Name)) v.Add("name", "cannot be blank.");
            if (req.Id is not null && req.Id != id) v.Add("id", "cannot be changed; trips reference it.");
            if (!v.IsValid) return v.Problem();
            if (req.Name is not null) op.Name = req.Name.Trim();
            if (req.IsVerified is not null) op.IsVerified = req.IsVerified.Value;
            // Deactivating hides the operator from new scheduling; existing trips keep it.
            if (req.IsActive is not null) op.IsActive = req.IsActive.Value;
            await db.SaveChangesAsync();
            return Results.Ok(Present(op));
        }).RequireRoles(OperatorRole.NetworkManager);
    }

    private static object Present(TransportOperator o) => new { id = o.Id, name = o.Name, rating = o.Rating, isVerified = o.IsVerified, isActive = o.IsActive };
}
