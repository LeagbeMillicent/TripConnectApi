namespace RiconRide.Api.Config;

public sealed class PlatformOptions
{
    public string AppName { get; set; } = "RiconRide";
    public string ContactPhone { get; set; } = "0539943305";
    public string SiteUrl { get; set; } = "http://localhost:3000";
    public string CmsUrl { get; set; } = "http://localhost:3001";
    public int SeatHoldMinutes { get; set; } = 10;
    /// <summary>How long an unpaid booking/rental/charter seat is kept before it is released.</summary>
    public int PaymentWindowMinutes { get; set; } = 15;
}

public sealed class JwtOptions
{
    public const string DevSecret = "dev-only-riconride-jwt-secret-change-me-0123456789";
    public string Secret { get; set; } = DevSecret;
    public string Issuer { get; set; } = "riconride-api";
    public string Audience { get; set; } = "riconride-cms";
    public int LifetimeHours { get; set; } = 10;
    public string CookieName { get; set; } = "rr_session";
}

public sealed class PaystackOptions
{
    public string? SecretKey { get; set; }
    /// <summary>
    /// Paystack signs webhooks with the secret key itself; this override exists
    /// only because the platform spec lists PAYSTACK_WEBHOOK_SECRET.
    /// </summary>
    public string? WebhookSecret { get; set; }
    public string BaseUrl { get; set; } = "https://api.paystack.co";
    public string? CallbackUrl { get; set; }
    /// <summary>
    /// Paystack requires an email on every transaction. Customers paying by phone
    /// alone get {phone}@{domain} — use a domain you own whose mail you discard.
    /// </summary>
    public string FallbackEmailDomain { get; set; } = "customers.riconride.com";
    /// <summary>Optional comma-separated source IPs allowed to call the webhook. Empty = signature check only.</summary>
    public string? WebhookAllowedIps { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey);
    public string SigningKey => string.IsNullOrWhiteSpace(WebhookSecret) ? SecretKey ?? "" : WebhookSecret;
}

public sealed class SmsOptions
{
    public string? ArkeselApiKey { get; set; }
    public string SenderId { get; set; } = "RiconRide";
    public string BaseUrl { get; set; } = "https://sms.arkesel.com";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ArkeselApiKey);
}

public sealed class WhatsAppOptions
{
    public string? PhoneNumberId { get; set; }
    public string? AccessToken { get; set; }
    /// <summary>
    /// Business-initiated WhatsApp messages must use a Meta-approved template;
    /// free text is rejected outside a 24h customer-initiated window. The
    /// template must take a single {{1}} body parameter.
    /// </summary>
    public string? TemplateName { get; set; }
    public string TemplateLanguage { get; set; } = "en";
    public string BaseUrl { get; set; } = "https://graph.facebook.com/v21.0";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PhoneNumberId) && !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(TemplateName);
}

public sealed class EmailOptions
{
    public string? ResendApiKey { get; set; }
    public string From { get; set; } = "RiconRide Support <support@riconride.com>";
    public string BaseUrl { get; set; } = "https://api.resend.com";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResendApiKey);
}

public sealed class SeedOperatorAccount
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = "";
}

public static class EnvAliases
{
    // Maps the env var names from the platform spec (§4) onto config sections,
    // so a .env written from the spec works without renaming anything.
    private static readonly Dictionary<string, string> Map = new()
    {
        ["NEXT_PUBLIC_APP_NAME"] = "Platform:AppName",
        ["NEXT_PUBLIC_CONTACT_PHONE"] = "Platform:ContactPhone",
        ["NEXT_PUBLIC_SITE_URL"] = "Platform:SiteUrl",
        ["NEXT_PUBLIC_CMS_URL"] = "Platform:CmsUrl",
        ["PAYSTACK_SECRET_KEY"] = "Paystack:SecretKey",
        ["PAYSTACK_WEBHOOK_SECRET"] = "Paystack:WebhookSecret",
        ["ARKESEL_SMS_API_KEY"] = "Sms:ArkeselApiKey",
        ["ARKESEL_SENDER_ID"] = "Sms:SenderId",
        ["WHATSAPP_PHONE_NUMBER_ID"] = "WhatsApp:PhoneNumberId",
        ["WHATSAPP_ACCESS_TOKEN"] = "WhatsApp:AccessToken",
        ["WHATSAPP_TEMPLATE_NAME"] = "WhatsApp:TemplateName",
        ["RESEND_API_KEY"] = "Email:ResendApiKey",
        ["SUPPORT_EMAIL_FROM"] = "Email:From",
        ["JWT_SECRET"] = "Jwt:Secret",
        ["DATABASE_URL"] = "ConnectionStrings:Default",
    };

    public static void Apply(ConfigurationManager config)
    {
        var values = Map
            .Select(kv => (kv.Value, Environment.GetEnvironmentVariable(kv.Key)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Item2))
            .ToDictionary(x => x.Value, x => x.Item2);
        if (values.Count > 0) config.AddInMemoryCollection(values);
    }
}

public static class StartupGuards
{
    /// <summary>
    /// Outside Development, refuse to boot with the source-controlled JWT secret
    /// (anyone could mint operator tokens) or without a payment gateway (bookings
    /// would "succeed" against the mock and seats would be sold unpaid).
    /// </summary>
    public static void Enforce(IHostEnvironment env, JwtOptions jwt, PaystackOptions paystack)
    {
        if (jwt.Secret.Length < 32)
            throw new InvalidOperationException("JWT_SECRET must be at least 32 characters.");
        if (env.IsDevelopment() || env.IsEnvironment("Testing")) return;

        var problems = new List<string>();
        if (jwt.Secret == JwtOptions.DevSecret) problems.Add("JWT_SECRET is the development default.");
        if (!paystack.IsConfigured) problems.Add("PAYSTACK_SECRET_KEY is not set (mock payments are Development-only).");
        if (problems.Count > 0)
            throw new InvalidOperationException("Refusing to start: " + string.Join(" ", problems));
    }
}
