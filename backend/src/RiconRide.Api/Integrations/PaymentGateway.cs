using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RiconRide.Api.Config;
using RiconRide.Api.Domain;

namespace RiconRide.Api.Integrations;

public sealed record GatewayInit(bool Ok, string? AuthorizationUrl, string? Error);

/// <summary>What the gateway says actually happened to a transaction.</summary>
public sealed record GatewayVerify(string Status, long AmountPesewas, string Currency);

public sealed record GatewayRefund(bool Ok, string? Error);

public interface IPaymentGateway
{
    string Name { get; }
    Task<GatewayInit> InitializeAsync(string reference, long amountPesewas, string phoneE164, string? email, string? network, CancellationToken ct);
    Task<GatewayVerify?> VerifyAsync(string reference, CancellationToken ct);
    Task<GatewayRefund> RefundAsync(string reference, long amountPesewas, CancellationToken ct);
    bool IsValidSignature(byte[] body, string? signature);
}

public static class WebhookSignature
{
    /// <summary>Paystack: hex HMAC-SHA512 of the raw body. Compared in constant time.</summary>
    public static bool Verify(string key, byte[] body, string? signature)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(signature)) return false;
        var expected = Compute(key, body);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public static string Compute(string key, byte[] body) =>
        Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(key), body));
}

public sealed class PaystackGateway(HttpClient http, IOptions<PaystackOptions> options, ILogger<PaystackGateway> log) : IPaymentGateway
{
    private readonly PaystackOptions _o = options.Value;
    public string Name => "paystack";

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, _o.BaseUrl.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.SecretKey);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    public async Task<GatewayInit> InitializeAsync(string reference, long amountPesewas, string phoneE164, string? email, string? network, CancellationToken ct)
    {
        var payload = new
        {
            email = email ?? $"{phoneE164.TrimStart('+')}@{_o.FallbackEmailDomain}",
            amount = amountPesewas, // Paystack amounts are in the subunit (pesewas)
            currency = "GHS",
            reference,
            callback_url = _o.CallbackUrl,
            channels = new[] { "mobile_money", "card" },
            metadata = new { phone = phoneE164, network },
        };
        try
        {
            using var res = await http.SendAsync(Request(HttpMethod.Post, "/transaction/initialize", payload), ct);
            var doc = await res.Content.ReadFromJsonAsync<PaystackEnvelope<InitData>>(ct);
            if (!res.IsSuccessStatusCode || doc?.Status != true || doc.Data is null)
            {
                log.LogWarning("Paystack initialize failed for {Reference}: {Status} {Message}", reference, (int)res.StatusCode, doc?.Message);
                return new GatewayInit(false, null, doc?.Message ?? "Payment gateway rejected the request.");
            }
            return new GatewayInit(true, doc.Data.AuthorizationUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogError(ex, "Paystack initialize error for {Reference}", reference);
            return new GatewayInit(false, null, "Payment gateway is unreachable.");
        }
    }

    public async Task<GatewayVerify?> VerifyAsync(string reference, CancellationToken ct)
    {
        try
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, "/transaction/verify/" + Uri.EscapeDataString(reference)), ct);
            if (!res.IsSuccessStatusCode) return null;
            var doc = await res.Content.ReadFromJsonAsync<PaystackEnvelope<VerifyData>>(ct);
            return doc?.Data is null ? null : new GatewayVerify(doc.Data.Status ?? "unknown", doc.Data.Amount, doc.Data.Currency ?? "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogError(ex, "Paystack verify error for {Reference}", reference);
            return null;
        }
    }

    public async Task<GatewayRefund> RefundAsync(string reference, long amountPesewas, CancellationToken ct)
    {
        try
        {
            using var res = await http.SendAsync(Request(HttpMethod.Post, "/refund", new { transaction = reference, amount = amountPesewas }), ct);
            var doc = await res.Content.ReadFromJsonAsync<PaystackEnvelope<JsonElement>>(ct);
            return res.IsSuccessStatusCode && doc?.Status == true
                ? new GatewayRefund(true, null)
                : new GatewayRefund(false, doc?.Message ?? "Refund rejected by gateway.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogError(ex, "Paystack refund error for {Reference}", reference);
            return new GatewayRefund(false, "Payment gateway is unreachable.");
        }
    }

    public bool IsValidSignature(byte[] body, string? signature) => WebhookSignature.Verify(_o.SigningKey, body, signature);

    private sealed record PaystackEnvelope<T>(bool Status, string? Message, T? Data);
    private sealed record InitData([property: JsonPropertyName("authorization_url")] string? AuthorizationUrl);
    private sealed record VerifyData(string? Status, long Amount, string? Currency);
}

/// <summary>
/// Development/test stand-in. It never moves money: transactions stay pending
/// until a signed webhook (or the dev-only /api/payments/mock/{ref}/complete
/// route) settles them. Startup refuses it outside Development.
/// </summary>
public sealed class MockPaymentGateway(IOptions<PaystackOptions> options) : IPaymentGateway
{
    public const string DevSigningKey = "sk_test_riconride_mock";
    private readonly ConcurrentDictionary<string, long> _amounts = new();
    public ConcurrentBag<(string Reference, long Amount)> Refunds { get; } = [];
    public string Name => "mock";

    public Task<GatewayInit> InitializeAsync(string reference, long amountPesewas, string phoneE164, string? email, string? network, CancellationToken ct)
    {
        _amounts[reference] = amountPesewas;
        return Task.FromResult(new GatewayInit(true, null, null));
    }

    public Task<GatewayVerify?> VerifyAsync(string reference, CancellationToken ct) =>
        Task.FromResult<GatewayVerify?>(_amounts.TryGetValue(reference, out var a) ? new GatewayVerify("pending", a, "GHS") : null);

    public Task<GatewayRefund> RefundAsync(string reference, long amountPesewas, CancellationToken ct)
    {
        Refunds.Add((reference, amountPesewas));
        return Task.FromResult(new GatewayRefund(true, null));
    }

    public bool IsValidSignature(byte[] body, string? signature)
    {
        var key = options.Value.SigningKey;
        return WebhookSignature.Verify(string.IsNullOrEmpty(key) ? DevSigningKey : key, body, signature);
    }
}
