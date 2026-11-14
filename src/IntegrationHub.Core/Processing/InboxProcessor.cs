using IntegrationHub.Core.Mapping;
using IntegrationHub.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace IntegrationHub.Core.Processing;

/// <summary>Claims a batch of inbox rows, maps them to the canonical model and applies them to the WMS.</summary>
public sealed class InboxProcessor(
    IInboxStore inbox,
    IWmsWriter wms,
    MapperRegistry mappers,
    RetryPolicy retryPolicy,
    ILogger<InboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(int batchSize, TimeSpan lease, CancellationToken ct)
    {
        var batch = await inbox.ClaimBatchAsync(batchSize, lease, ct);
        foreach (var message in batch)
        {
            await ProcessAsync(message, ct);
        }

        return batch.Count;
    }

    private async Task ProcessAsync(InboxMessage message, CancellationToken ct)
    {
        using var scope = logger.BeginScope("CorrelationId:{CorrelationId} InboxId:{InboxId}", message.CorrelationId, message.Id);

        try
        {
            var mapper = mappers.Get(message.SourceSystem);
            var applied = message.MessageType switch
            {
                MessageTypes.Order => await wms.AcceptOrderAsync(message, mapper.MapOrder(message.Payload), ct),
                MessageTypes.Asn => await wms.ReceiveAsnAsync(message, mapper.MapAsn(message.Payload), ct),
                _ => throw new MappingException($"Unsupported message type '{message.MessageType}'."),
            };

            logger.LogInformation(
                applied ? "Processed {MessageType} from {SourceSystem} (attempt {Attempt})"
                        : "Skipped duplicate {MessageType} from {SourceSystem} (attempt {Attempt})",
                message.MessageType, message.SourceSystem, message.AttemptCount);
        }
        catch (MappingException ex)
        {
            logger.LogWarning(ex, "Permanent failure, dead-lettering {MessageType} from {SourceSystem}", message.MessageType, message.SourceSystem);
            await inbox.DeadLetterAsync(message, ex.Message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (retryPolicy.ShouldDeadLetter(message.AttemptCount))
            {
                logger.LogError(ex, "Attempt {Attempt} failed, giving up and dead-lettering", message.AttemptCount);
                await inbox.DeadLetterAsync(message, ex.Message, ct);
                return;
            }

            var delay = retryPolicy.NextDelay(message.AttemptCount, Random.Shared.NextDouble());
            logger.LogWarning(ex, "Attempt {Attempt} failed, retrying in {Delay}", message.AttemptCount, delay);
            await inbox.ScheduleRetryAsync(message, delay, ex.Message, ct);
        }
    }
}
