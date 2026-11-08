using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RiconRide.Api.Config;
using RiconRide.Api.Domain;

namespace RiconRide.Api.Integrations;

/// <summary>A message to a traveller. Phone is E.164; Email optional.</summary>
public sealed record Notification(string PhoneE164, string? Email, string Subject, string Text);

public interface INotifier
{
    /// <summary>Queues delivery; never throws and never blocks the request on a provider.</summary>
    void Enqueue(Notification n);
}

public sealed class QueuedNotifier : INotifier
{
    private readonly Channel<Notification> _channel = Channel.CreateBounded<Notification>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<Notification> Reader => _channel.Reader;

    public void Enqueue(Notification n) => _channel.Writer.TryWrite(n);
}

/// <summary>Drains the queue to every configured channel. One channel failing does not stop the others.</summary>
public sealed class NotificationDispatcher(
    QueuedNotifier queue, IHttpClientFactory httpFactory,
    IOptions<SmsOptions> sms, IOptions<WhatsAppOptions> wa, IOptions<EmailOptions> email,
    ILogger<NotificationDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var n in queue.Reader.ReadAllAsync(stop))
        {
            await Try("sms", sms.Value.IsConfigured, n, () => SendSms(n, stop));
            await Try("whatsapp", wa.Value.IsConfigured, n, () => SendWhatsApp(n, stop));
            if (n.Email is not null) await Try("email", email.Value.IsConfigured, n, () => SendEmail(n, stop));
        }
    }

    private async Task Try(string channel, bool configured, Notification n, Func<Task<HttpResponseMessage>> send)
    {
        var to = GhanaPhone.Mask(n.PhoneE164);
        if (!configured)
        {
            // Logged, not pretended: with no provider keys nothing is delivered.
            log.LogInformation("[{Channel}] not configured — NOT sent to {To}: {Subject}", channel, to, n.Subject);
            return;
        }
        try
        {
            using var res = await send();
            if (!res.IsSuccessStatusCode)
                log.LogWarning("[{Channel}] delivery to {To} failed: {Status}", channel, to, (int)res.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[{Channel}] delivery to {To} errored", channel, to);
        }
    }

    private Task<HttpResponseMessage> SendSms(Notification n, CancellationToken ct)
    {
        var o = sms.Value;
        var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/api/v2/sms/send")
        {
            Content = JsonContent.Create(new { sender = o.SenderId, message = n.Text, recipients = new[] { n.PhoneE164.TrimStart('+') } }),
        };
        req.Headers.Add("api-key", o.ArkeselApiKey);
        return httpFactory.CreateClient("notify").SendAsync(req, ct);
    }

    private Task<HttpResponseMessage> SendWhatsApp(Notification n, CancellationToken ct)
    {
        var o = wa.Value;
        var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/{o.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                to = n.PhoneE164.TrimStart('+'),
                type = "template",
                template = new
                {
                    name = o.TemplateName,
                    language = new { code = o.TemplateLanguage },
                    components = new[] { new { type = "body", parameters = new[] { new { type = "text", text = n.Text } } } },
                },
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);
        return httpFactory.CreateClient("notify").SendAsync(req, ct);
    }

    private Task<HttpResponseMessage> SendEmail(Notification n, CancellationToken ct)
    {
        var o = email.Value;
        var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/emails")
        {
            Content = JsonContent.Create(new { from = o.From, to = new[] { n.Email }, subject = n.Subject, text = n.Text }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ResendApiKey);
        return httpFactory.CreateClient("notify").SendAsync(req, ct);
    }
}
