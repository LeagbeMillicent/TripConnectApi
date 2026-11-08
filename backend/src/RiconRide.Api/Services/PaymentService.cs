using Microsoft.EntityFrameworkCore;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;
using RiconRide.Api.Integrations;

namespace RiconRide.Api.Services;

public sealed record PaymentStart(Payment Payment, string? Error);

public sealed class PaymentService(
    AppDbContext db, IPaymentGateway gateway, INotifier notifier, TicketSigner tickets,
    TimeProvider clock, ILogger<PaymentService> log)
{
    /// <summary>
    /// Records a pending payment and opens it at the gateway. The amount always
    /// comes from the server-side subject (booking/member/rental), never the client.
    /// </summary>
    public async Task<PaymentStart> StartAsync(PaymentKind kind, Guid subjectId, long amountPesewas,
        string phoneE164, string? email, string? network, CancellationToken ct)
    {
        var payment = new Payment
        {
            Reference = Codes.PaymentReference(), Kind = kind, SubjectId = subjectId, AmountPesewas = amountPesewas,
            Provider = gateway.Name, CustomerPhone = phoneE164, CustomerEmail = email, Network = network,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        var init = await gateway.InitializeAsync(payment.Reference, amountPesewas, phoneE164, email, network, ct);
        if (init.Ok) payment.AuthorizationUrl = init.AuthorizationUrl;
        else { payment.Status = PaymentStatus.Failed; payment.FailureReason = init.Error; }
        await db.SaveChangesAsync(ct);
        return new PaymentStart(payment, init.Ok ? null : init.Error);
    }

    /// <summary>
    /// Applies a gateway-confirmed success. Idempotent (webhooks are retried and
    /// verify can race the webhook) and locks the payment row so two deliveries
    /// cannot both fulfil. If the subject lapsed and its seat/vehicle/place was
    /// taken meanwhile, the money is flagged RefundDue rather than double-selling.
    /// </summary>
    public async Task<PaymentStatus?> ApplySuccessAsync(string reference, long paidPesewas, string currency, CancellationToken ct)
    {
        await Expiry.PurgeAsync(db, clock, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = await db.Payments.FromSql($"SELECT * FROM payments WHERE reference = {reference} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (payment is null) return null;
        if (payment.Status is PaymentStatus.Success or PaymentStatus.RefundDue) return payment.Status;

        if (paidPesewas != payment.AmountPesewas || !string.Equals(currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            // Never fulfil on a short or foreign-currency payment.
            log.LogError("Payment {Reference} amount mismatch: expected {Expected} {Cur}, gateway reported {Paid} {PaidCur}",
                reference, payment.AmountPesewas, payment.Currency, paidPesewas, currency);
            payment.Status = PaymentStatus.RefundDue;
            payment.FailureReason = "Amount or currency did not match the order.";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return payment.Status;
        }

        payment.PaidAt = clock.GetUtcNow();
        Notification? message = payment.Kind switch
        {
            PaymentKind.Ticket => await FulfilBooking(payment, ct),
            PaymentKind.Charter => await FulfilMember(payment, ct),
            PaymentKind.Rental => await FulfilRental(payment, ct),
            _ => null,
        };
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (message is not null) notifier.Enqueue(message);
        return payment.Status;
    }

    public void MarkFailed(Payment p, string reason)
    {
        if (p.Status != PaymentStatus.Pending) return;
        p.Status = PaymentStatus.Failed;
        p.FailureReason = reason;
    }

    private async Task<Notification?> FulfilBooking(Payment payment, CancellationToken ct)
    {
        var booking = await db.Bookings.FromSql($"SELECT * FROM bookings WHERE id = {payment.SubjectId} FOR UPDATE")
            .Include(b => b.Passengers).Include(b => b.Trip).ThenInclude(t => t.OriginTerminal).SingleAsync(ct);

        var fulfillable = booking.Status is BookingStatus.PendingPayment or BookingStatus.Expired
                          && booking.Trip.Status is not TripStatus.Cancelled;
        if (fulfillable && await SecureSeats(booking, ct))
        {
            payment.Status = PaymentStatus.Success;
            booking.Status = BookingStatus.Confirmed;
            booking.ConfirmedAt = clock.GetUtcNow();
            var t = booking.Trip;
            return new Notification(booking.ContactPhone, payment.CustomerEmail, $"RiconRide ticket {booking.BookingNumber}",
                $"RiconRide: ticket {booking.BookingNumber} confirmed. {t.OriginCity} to {t.DestinationCity}, " +
                $"{Formats.Date(t.DepartureDate)} {Formats.Time(t.DepartureTime)} from {t.OriginTerminal.Name}. " +
                $"Seats {string.Join(",", booking.Passengers.Select(p => p.SeatNumber).Order())}. " +
                $"Paid {Money.Format(booking.TotalPesewas)}. Boarding pass: {tickets.TicketLink(booking.BookingNumber)}");
        }

        payment.Status = PaymentStatus.RefundDue;
        payment.FailureReason = "Booking could not be fulfilled (already paid, cancelled, or seats taken after expiry).";
        if (booking.Status is BookingStatus.Expired) booking.Status = BookingStatus.RefundDue;
        return new Notification(booking.ContactPhone, payment.CustomerEmail, "RiconRide payment received — refund due",
            $"RiconRide: we received {Money.Format(payment.AmountPesewas)} for {booking.BookingNumber} but could not confirm the seats. A refund is being processed.");
    }

    /// <summary>Keeps this booking's seats; re-takes them if expiry released them and nobody else took them.</summary>
    private async Task<bool> SecureSeats(Booking booking, CancellationToken ct)
    {
        var seats = booking.Passengers.Select(p => p.SeatNumber).ToList();
        var kept = await db.SeatReservations.Where(r => r.BookingId == booking.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, (DateTimeOffset?)null), ct);
        if (kept == seats.Count) return true;

        var now = clock.GetUtcNow();
        var taken = await db.SeatReservations.AnyAsync(r => r.TripId == booking.TripId && seats.Contains(r.SeatNumber)
            && r.BookingId != booking.Id && (r.ExpiresAt == null || r.ExpiresAt >= now), ct);
        if (taken) return false;
        var have = await db.SeatReservations.Where(r => r.BookingId == booking.Id).Select(r => r.SeatNumber).ToListAsync(ct);
        db.SeatReservations.AddRange(seats.Except(have).Select(s => new SeatReservation { TripId = booking.TripId, SeatNumber = s, BookingId = booking.Id }));
        return true;
    }

    private async Task<Notification?> FulfilMember(Payment payment, CancellationToken ct)
    {
        var member = await db.GroupMembers.SingleAsync(m => m.Id == payment.SubjectId, ct);
        var group = await db.Groups.FromSql($"SELECT * FROM group_charters WHERE id = {member.GroupId} FOR UPDATE").SingleAsync(ct);
        var now = clock.GetUtcNow();
        var open = group.Status is GroupStatus.Planning or GroupStatus.Confirmed;
        var ok = member.Status == MemberStatus.PendingPayment
                 || (member.Status == MemberStatus.Expired
                     && await db.GroupMembers.Occupying(group.Id, now).CountAsync(ct) < group.TargetTravelers);
        if (open && ok)
        {
            payment.Status = PaymentStatus.Success;
            member.Status = MemberStatus.Paid;
            return new Notification(member.Phone, payment.CustomerEmail, $"RiconRide charter {group.InviteCode}",
                $"RiconRide: your place on \"{group.Name}\" ({group.InviteCode}) is confirmed. {Formats.Date(group.TravelDate)} " +
                $"{Formats.Time(group.PreferredDepartureTime)}, pickup: {group.PickupLocation}. Paid {Money.Format(member.FarePesewas)}.");
        }
        payment.Status = PaymentStatus.RefundDue;
        payment.FailureReason = "Charter place could not be confirmed (group closed, full, or already paid).";
        return new Notification(member.Phone, payment.CustomerEmail, "RiconRide payment received — refund due",
            $"RiconRide: we received {Money.Format(payment.AmountPesewas)} for charter {group.InviteCode} but the place could not be confirmed. A refund is being processed.");
    }

    private async Task<Notification?> FulfilRental(Payment payment, CancellationToken ct)
    {
        var rental = await db.Rentals.Include(r => r.Vehicle).SingleAsync(r => r.Id == payment.SubjectId, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM vehicles WHERE id = {rental.VehicleId} FOR UPDATE", ct);
        var now = clock.GetUtcNow();
        var ok = rental.Status == RentalStatus.PendingPayment
                 || (rental.Status == RentalStatus.Expired
                     && !await db.Rentals.Blocking(rental.VehicleId, rental.StartDate, rental.EndDate, now).AnyAsync(r => r.Id != rental.Id, ct));
        if (ok)
        {
            payment.Status = PaymentStatus.Success;
            rental.Status = RentalStatus.Confirmed;
            rental.DepositStatus = DepositStatus.Held;
            return new Notification(rental.CustomerPhone, rental.CustomerEmail, $"RiconRide rental {rental.RentalNumber}",
                $"RiconRide: rental {rental.RentalNumber} confirmed — {rental.Vehicle.Name}, {Formats.Date(rental.StartDate)} to " +
                $"{Formats.Date(rental.EndDate)}, pickup {rental.PickupPoint}. Paid {Money.Format(rental.TotalPesewas)} incl. " +
                $"{Money.Format(rental.DepositPesewas)} refundable deposit.");
        }
        payment.Status = PaymentStatus.RefundDue;
        payment.FailureReason = "Rental could not be confirmed (vehicle booked meanwhile or already paid).";
        return new Notification(rental.CustomerPhone, rental.CustomerEmail, "RiconRide payment received — refund due",
            $"RiconRide: we received {Money.Format(payment.AmountPesewas)} for {rental.RentalNumber} but the vehicle is no longer available for those dates. A refund is being processed.");
    }
}
