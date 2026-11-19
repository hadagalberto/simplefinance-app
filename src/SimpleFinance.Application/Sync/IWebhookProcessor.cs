namespace SimpleFinance.Application.Sync;

/// <summary>The Hangfire-invoked contract 02-05's job class implements.</summary>
public interface IWebhookProcessor
{
    Task ProcessAsync(Guid webhookEventId, CancellationToken ct);
}
