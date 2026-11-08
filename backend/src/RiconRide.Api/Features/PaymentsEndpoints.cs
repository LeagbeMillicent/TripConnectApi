using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RiconRide.Api.Config;
using RiconRide.Api.Data;
using RiconRide.Api.Domain;
using RiconRide.Api.Integrations;
using RiconRide.Api.Services;

namespace RiconRide.Api.Features;

public static class PaymentsEndpoints
{
    public sealed record InitializeRequest(
        string? ReferenceType, string? ReferenceId, decimal? Amount, string? Currency,
        string? CustomerEmail, string? CustomerPhone, string? PaymentNetwork);

    private static readonly IReadOnlyDictionary<string, PaymentKind> Kinds = new Dictionary<string, PaymentKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["ticket"] = PaymentKind.Ticket, ["charter"] = PaymentKind.Charter, ["rental"] = PaymentKind.Rental,
    };

    public static object Present(Payment p) => new
    {
        reference = p.Reference,
        status = p.Status switch { PaymentStatus.RefundDue => "refund_due", PaymentStatus.Success => "paid", var s => s.ToString().ToLowerInvariant() },
        amount = Money.ToCedis(p.AmountPesewas),
        currency = p.Currency,
        provider = p.Provider,
        referenceType = p.Kind.ToString().ToLowerInvariant(),
        authorizationUrl = p.AuthorizationUrl,
        paidAt = p.PaidAt,
    };

    public static void MapPayments(this IEndpointRouteBuilder app, bool mockGateway)
    {
        var g = app.MapGroup("/api/payments").WithTags("Payments");

        g.MapPost("/initialize", async (InitializeRequest req, AppDbContext db, PaymentService payments, TimeProvider clock, CancellationToken ct) =>
        {
            var v = new Validator().Require("referenceId", req.ReferenceId, 80).Optional("customerEmail", req.CustomerEmail, 254);
            var kind = v.OneOf("referenceType", req.ReferenceType, Kinds);
            var phone = v.Phone("customerPhone", req.CustomerPhone);
            var network = v.OptionalChoice("paymentNetwork", req.PaymentNetwork, Choices.MomoProviders);
            if (req.Currency is not null && !req.Currency.Equals("GHS", StringComparison.OrdinalIgnoreCase)) v.Add("currency", "only GHS is supported.");
            if (!v.IsValid) return v.Problem();

            await Expiry.PurgeAsync(db, clock, ct);
            var id = req.ReferenceId!.Trim();
            // The amount is looked up from the order, never taken from the client.
            (Guid Id, long Amount)? subject = kind switch
            {
                PaymentKind.Ticket => await db.Bookings.Where(b => b.BookingNumber == id && b.Status == BookingStatus.PendingPayment)
                    .Select(b => new { b.Id, b.TotalPesewas }).FirstOrDefaultAsync(ct) is { } b ? (b.Id, b.TotalPesewas) : null,
                PaymentKind.Charter => Guid.TryParse(id, out var mid)
                    && await db.GroupMembers.Where(m => m.Id == mid && m.Status == MemberStatus.PendingPayment)
                        .Select(m => new { m.Id, m.FarePesewas }).FirstOrDefaultAsync(ct) is { } m ? (m.Id, m.FarePesewas) : null,
                PaymentKind.Rental => await db.Rentals.Where(r => r.RentalNumber == id && r.Status == RentalStatus.PendingPayment)
                    .Select(r => new { r.Id, r.TotalPesewas }).FirstOrDefaultAsync(ct) is { } r ? (r.Id, r.TotalPesewas) : null,
                _ => null,
            };
            if (subject is null) return Problems.NotFound("Unpaid order (it may have expired or already been paid)");
            if (req.Amount is not null && Money.ToPesewas(req.Amount.Value) != subject.Value.Amount)
                return Problems.Unprocessable($"Amount does not match the order total of {Money.Format(subject.Value.Amount)}.");

            var start = await payments.StartAsync(kind, subject.Value.Id, subject.Value.Amount, phone, req.CustomerEmail?.Trim(), network, ct);
            return start.Error is null ? Results.Ok(Present(start.Payment)) : Problems.BadGateway(start.Error);
        }).RequireRateLimiting("public-write");

        g.MapPost("/webhook", async (HttpContext http, IPaymentGateway gateway, PaymentService payments, AppDbContext db,
            IOptions<PaystackOptions> opt, ILogger<PaymentService> log, CancellationToken ct) =>
        {
            if (!IpAllowed(http.Connection.RemoteIpAddress, opt.Value.WebhookAllowedIps)) return Results.StatusCode(403);
            // The signature is over the exact raw bytes, so read them before any JSON parsing.
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            var raw = ms.ToArray();
            if (!gateway.IsValidSignature(raw, http.Request.Headers["x-paystack-signature"])) return Results.Unauthorized();

            using var doc = JsonDocument.Parse(raw);
            var evt = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("reference", out var refEl))
                return Results.Ok();
            var reference = refEl.GetString() ?? "";

            if (evt == "charge.success")
            {
                var amount = data.TryGetProperty("amount", out var a) ? a.GetInt64() : -1;
                var currency = data.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
                var status = await payments.ApplySuccessAsync(reference, amount, currency, ct);
                if (status is null) log.LogWarning("Webhook for unknown payment reference {Reference}", reference);
            }
            else if (evt == "charge.failed")
            {
                var p = await db.Payments.SingleOrDefaultAsync(x => x.Reference == reference, ct);
                if (p is not null) { payments.MarkFailed(p, "Gateway reported the charge failed."); await db.SaveChangesAsync(ct); }
            }
            // 200 for anything correctly signed, so the gateway stops retrying events we don't act on.
            return Results.Ok();
        });

        g.MapGet("/verify/{reference}", async (string reference, AppDbContext db, IPaymentGateway gateway, PaymentService payments, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Reference == reference, ct);
            if (p is null) return Problems.NotFound("Payment");
            if (p.Status == PaymentStatus.Pending)
            {
                // Covers a missed or delayed webhook: ask the gateway directly.
                var check = await gateway.VerifyAsync(reference, ct);
                if (check?.Status == "success") await payments.ApplySuccessAsync(reference, check.AmountPesewas, check.Currency, ct);
                else if (check?.Status is "failed" or "abandoned" or "reversed")
                {
                    var tracked = await db.Payments.SingleAsync(x => x.Reference == reference, ct);
                    payments.MarkFailed(tracked, $"Gateway status: {check.Status}.");
                    await db.SaveChangesAsync(ct);
                }
                p = await db.Payments.AsNoTracking().SingleAsync(x => x.Reference == reference, ct);
            }
            return Results.Ok(Present(p));
        });

        if (mockGateway)
        {
            // Dev only: registered solely while the mock gateway is active, so it
            // cannot exist in an environment that takes real money.
            g.MapPost("/mock/{reference}/complete", async (string reference, AppDbContext db, PaymentService payments, CancellationToken ct) =>
            {
                var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Reference == reference, ct);
                if (p is null) return Problems.NotFound("Payment");
                await payments.ApplySuccessAsync(reference, p.AmountPesewas, p.Currency, ct);
                return Results.Ok(Present(await db.Payments.AsNoTracking().SingleAsync(x => x.Reference == reference, ct)));
            }).WithTags("Development");
        }
    }

    private static bool IpAllowed(IPAddress? ip, string? allowList)
    {
        if (string.IsNullOrWhiteSpace(allowList)) return true;
        if (ip is null) return false;
        var addr = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        return allowList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(a => IPAddress.TryParse(a, out var allowed) && allowed.Equals(addr));
    }
}
