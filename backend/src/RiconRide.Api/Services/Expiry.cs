using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RiconRide.Api.Config;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;

namespace RiconRide.Api.Services;

/// <summary>
/// Releases everything whose payment window lapsed. Run lazily before any
/// read or write that depends on availability, so an abandoned checkout can
/// never hold a seat, a vehicle or a charter place longer than its window.
/// </summary>
public static class Expiry
{
    public static async Task PurgeAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // Order matters: mark bookings expired BEFORE deleting their seats, so
        // a booking is never "pending" while its seats are already gone.
        await db.Bookings.Where(b => b.Status == BookingStatus.PendingPayment && b.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BookingStatus.Expired), ct);
        await db.SeatReservations.Where(r => r.ExpiresAt != null && r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await db.GroupMembers.Where(m => m.Status == MemberStatus.PendingPayment && m.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MemberStatus.Expired), ct);
        await db.Rentals.Where(r => r.Status == RentalStatus.PendingPayment && r.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RentalStatus.Expired), ct);
    }

    /// <summary>Rentals that block a vehicle for an overlapping date range.</summary>
    public static IQueryable<Rental> Blocking(this IQueryable<Rental> q, Guid vehicleId, DateOnly start, DateOnly end, DateTimeOffset now) =>
        q.Where(r => r.VehicleId == vehicleId
                     && r.StartDate <= end && r.EndDate >= start
                     && (r.Status == RentalStatus.Confirmed
                         || (r.Status == RentalStatus.PendingPayment && r.ExpiresAt >= now)));

    /// <summary>Members occupying a charter place: paid, or pending inside their window.</summary>
    public static IQueryable<GroupMember> Occupying(this IQueryable<GroupMember> q, Guid groupId, DateTimeOffset now) =>
        q.Where(m => m.GroupId == groupId
                     && (m.Status == MemberStatus.Paid || (m.Status == MemberStatus.PendingPayment && m.ExpiresAt >= now)));
}

/// <summary>
/// Signs booking numbers so a QR code or ticket link cannot be forged by
/// guessing a booking number. Keyed off the JWT secret via a labelled HMAC so
/// there is one secret to rotate, but tokens of one kind never verify as the other.
/// </summary>
public sealed class TicketSigner(IOptions<JwtOptions> jwt, IOptions<PlatformOptions> platform)
{
    private readonly byte[] _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.Secret), "riconride-ticket-v1"u8.ToArray());

    public string Sign(string bookingNumber) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(bookingNumber)))[..20];

    public bool Verify(string bookingNumber, string? token) =>
        token is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(bookingNumber)), Encoding.ASCII.GetBytes(token.Trim().ToLowerInvariant()));

    public string TicketLink(string bookingNumber) =>
        $"{platform.Value.SiteUrl.TrimEnd('/')}/ticket/{bookingNumber}?token={Sign(bookingNumber)}";

    public string QrData(Booking b)
    {
        var seats = string.Join(",", b.Passengers.Select(p => p.SeatNumber).Order());
        var lead = b.Passengers.OrderBy(p => p.SeatNumber).First().FullName.Replace("|", " ");
        return $"{b.BookingNumber}|{b.Trip.OriginCity}-{b.Trip.DestinationCity}|SEATS:{seats}|{lead}|{Money.Format(b.TotalPesewas)}|SIG:{Sign(b.BookingNumber)}";
    }

    /// <summary>Returns the booking number if the QR payload's signature checks out.</summary>
    public string? ReadQr(string? qr)
    {
        if (string.IsNullOrWhiteSpace(qr)) return null;
        var parts = qr.Split('|');
        var sig = parts.LastOrDefault(p => p.StartsWith("SIG:", StringComparison.Ordinal))?[4..];
        return parts.Length > 1 && Verify(parts[0], sig) ? parts[0] : null;
    }
}
