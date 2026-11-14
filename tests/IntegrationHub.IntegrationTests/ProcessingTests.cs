using System.Collections.Concurrent;
using System.Net;
using System.Text;
using IntegrationHub.Core.Canonical;
using IntegrationHub.Core.Idempotency;
using IntegrationHub.Core.Mapping;
using IntegrationHub.Core.Messaging;
using IntegrationHub.Core.Outbound;
using IntegrationHub.Core.Processing;
using IntegrationHub.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationHub.IntegrationTests;

public sealed class ProcessingTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly RetryPolicy ImmediateRetry = new() { MaxAttempts = 3, BaseDelay = TimeSpan.Zero };
    private readonly InboxRepository _inbox = new(sql.Db);
    private readonly WmsRepository _wms = new(sql.Db);
    private readonly ProductRepository _products = new(sql.Db);

    public async ValueTask InitializeAsync() => await sql.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_workers_process_every_message_exactly_once()
    {
        await SeedProductAsync("SKU-1", onHand: 10_000);
        for (var i = 1; i <= 200; i++)
        {
            await StageAsync("erp", MessageTypes.Order, ErpOrder($"SO-{i}", ("SKU-1", 2)), $"key-{i}");
        }

        // Three competing workers, each looping until nothing is left to claim.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            var processor = Processor(_wms, ImmediateRetry);
            while (await processor.ProcessBatchAsync(7, Lease, CancellationToken.None) > 0)
            {
            }
        })));

        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.orders")).ShouldBe(200);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbox WHERE status = N'Processed' AND attempt_count = 1")).ShouldBe(200);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox WHERE event_type = N'OrderAccepted'")).ShouldBe(200);
        (await sql.ScalarAsync<int>("SELECT on_hand FROM dbo.products WHERE sku = N'SKU-1'")).ShouldBe(10_000 - (200 * 2));
    }

    [Fact]
    public async Task Order_and_its_outbox_events_are_written_in_one_transaction()
    {
        await SeedProductAsync("SKU-1", onHand: 50);
        await SeedProductAsync("SKU-2", onHand: 50);
        var inboxId = await StageAsync("erp", MessageTypes.Order, ErpOrder("SO-OK", ("SKU-1", 3), ("SKU-2", 1), ("SKU-1", 2)), "k-ok");

        await Processor(_wms, ImmediateRetry).ProcessBatchAsync(10, Lease, CancellationToken.None);

        var events = await sql.QueryAsync<(string EventType, string CorrelationId)>("SELECT event_type, correlation_id FROM dbo.outbox ORDER BY id");
        events.Select(e => e.EventType).ShouldBe(["OrderAccepted", "StockLevelChanged", "StockLevelChanged"]);
        events.ShouldAllBe(e => e.CorrelationId == "corr-k-ok");
        (await sql.ScalarAsync<string>("SELECT status FROM dbo.inbox WHERE id = @inboxId", new { inboxId })).ShouldBe("Processed");
        (await sql.ScalarAsync<int>("SELECT on_hand FROM dbo.products WHERE sku = N'SKU-1'")).ShouldBe(45);
    }

    [Fact]
    public async Task Failure_mid_transaction_rolls_back_order_stock_and_outbox_together()
    {
        await SeedProductAsync("SKU-1", onHand: 50);
        // SKU-1 is updated first, then SKU-404 does not exist -> the whole transaction must roll back.
        var inboxId = await StageAsync("erp", MessageTypes.Order, ErpOrder("SO-BAD", ("SKU-1", 5), ("SKU-404", 1)), "k-bad");

        await Processor(_wms, ImmediateRetry).ProcessBatchAsync(10, Lease, CancellationToken.None);

        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.orders")).ShouldBe(0);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox")).ShouldBe(0);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.stock_movements")).ShouldBe(0);
        (await sql.ScalarAsync<int>("SELECT on_hand FROM dbo.products WHERE sku = N'SKU-1'")).ShouldBe(50);

        var (status, lastError) = (await sql.QueryAsync<(string, string)>("SELECT status, last_error FROM dbo.inbox WHERE id = @inboxId", new { inboxId }))[0];
        status.ShouldBe("Failed"); // unknown SKU is transient: master data may still arrive
        lastError.ShouldContain("SKU-404");
    }

    [Fact]
    public async Task Transient_failures_are_retried_then_dead_lettered_and_can_be_replayed()
    {
        var inboxId = await StageAsync("erp", MessageTypes.Order, ErpOrder("SO-R", ("SKU-1", 1)), "k-retry");
        var processor = Processor(new AlwaysFailingWriter(), ImmediateRetry);

        await processor.ProcessBatchAsync(10, Lease, CancellationToken.None);
        (await StatusAsync(inboxId)).ShouldBe(("Failed", 1));

        await processor.ProcessBatchAsync(10, Lease, CancellationToken.None);
        await processor.ProcessBatchAsync(10, Lease, CancellationToken.None);
        (await StatusAsync(inboxId)).ShouldBe(("DeadLettered", 3));
        (await processor.ProcessBatchAsync(10, Lease, CancellationToken.None)).ShouldBe(0); // dead letters are never claimed

        var deadLetter = (await _inbox.ListDeadLettersAsync(10, CancellationToken.None)).ShouldHaveSingleItem();
        deadLetter.LastError.ShouldBe("WMS is down");

        (await _inbox.ReplayAsync(inboxId, CancellationToken.None)).ShouldBeTrue();
        (await StatusAsync(inboxId)).ShouldBe(("Received", 0));
        (await _inbox.ReplayAsync(inboxId, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Unmappable_payload_is_dead_lettered_immediately()
    {
        var inboxId = await StageAsync("erp", MessageTypes.Order, """{"DocNo":"SO-U","DocDate":"14.11.2026","Lines":[{"ItemNo":"A","Qty":1,"Uom":"BOX"}]}""", "k-unmappable");

        await Processor(_wms, ImmediateRetry).ProcessBatchAsync(10, Lease, CancellationToken.None);

        (await StatusAsync(inboxId)).ShouldBe(("DeadLettered", 1));
    }

    [Fact]
    public async Task Same_business_order_under_a_new_idempotency_key_is_not_applied_twice()
    {
        await SeedProductAsync("SKU-1", onHand: 10);
        await StageAsync("erp", MessageTypes.Order, ErpOrder("SO-DUP", ("SKU-1", 1)), "k-a");
        await StageAsync("erp", MessageTypes.Order, ErpOrder("SO-DUP", ("SKU-1", 1)), "k-b");

        await Processor(_wms, ImmediateRetry).ProcessBatchAsync(10, Lease, CancellationToken.None);

        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbox WHERE status = N'Processed'")).ShouldBe(2);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.orders")).ShouldBe(1);
        (await sql.ScalarAsync<int>("SELECT on_hand FROM dbo.products WHERE sku = N'SKU-1'")).ShouldBe(9);
    }

    [Fact]
    public async Task Asn_receipt_increases_stock_and_emits_stock_event()
    {
        await SeedProductAsync("SKU-1", onHand: 0);
        await StageAsync("erp", MessageTypes.Asn, """{"AsnNo":"ASN-1","ExpectedDate":"20.11.2026","Lines":[{"ItemNo":"SKU-1","Qty":2,"Uom":"DZ"}]}""", "k-asn");

        await Processor(_wms, ImmediateRetry).ProcessBatchAsync(10, Lease, CancellationToken.None);

        (await sql.ScalarAsync<int>("SELECT on_hand FROM dbo.products WHERE sku = N'SKU-1'")).ShouldBe(24);
        (await sql.ScalarAsync<string>("SELECT event_type FROM dbo.outbox")).ShouldBe("StockLevelChanged");
    }

    [Fact]
    public async Task Outbox_dispatch_marks_delivered_events_and_retries_failed_ones()
    {
        await sql.ExecuteAsync("INSERT dbo.outbox (event_type, payload, correlation_id) VALUES (N'OrderAccepted', N'{\"orderId\":1}', N'corr-out');");
        var options = new OutboxOptions { Subscribers = [new Subscriber { Name = "test", Url = new Uri("http://subscriber.test/hook"), Events = ["OrderAccepted"] }] };

        var failing = new RecordingHandler(HttpStatusCode.InternalServerError);
        await new OutboxDispatcher(new OutboxRepository(sql.Db), new HttpClient(failing), options, ImmediateRetry, NullLogger<OutboxDispatcher>.Instance)
            .DispatchBatchAsync(10, Lease, CancellationToken.None);
        (await sql.ScalarAsync<string>("SELECT status FROM dbo.outbox")).ShouldBe("Failed");

        var healthy = new RecordingHandler(HttpStatusCode.OK);
        await new OutboxDispatcher(new OutboxRepository(sql.Db), new HttpClient(healthy), options, ImmediateRetry, NullLogger<OutboxDispatcher>.Instance)
            .DispatchBatchAsync(10, Lease, CancellationToken.None);

        (await sql.ScalarAsync<string>("SELECT status FROM dbo.outbox")).ShouldBe("Dispatched");
        var request = healthy.Requests.ShouldHaveSingleItem();
        request.Headers.GetValues("X-Correlation-Id").ShouldBe(["corr-out"]);
        request.Headers.Contains("X-Event-Id").ShouldBeTrue();
    }

    private InboxProcessor Processor(IWmsWriter writer, RetryPolicy retry) =>
        new(_inbox, writer, new MapperRegistry([new ErpMessageMapper(), new ShopMessageMapper()]), retry, NullLogger<InboxProcessor>.Instance);

    private async Task<long> StageAsync(string source, string type, string payload, string key)
    {
        var result = await _inbox.ReceiveAsync(
            new InboundMessage(source, type, payload, IdempotencyDecision.HashPayload(Encoding.UTF8.GetBytes(payload)), key, $"corr-{key}"),
            CancellationToken.None);
        return result.InboxId;
    }

    private async Task SeedProductAsync(string sku, int onHand)
    {
        await _products.UpsertAsync([new ProductRecord(sku, sku, "EA", null, null)], CancellationToken.None);
        await sql.ExecuteAsync("UPDATE dbo.products SET on_hand = @onHand WHERE sku = @sku", new { sku, onHand });
    }

    private async Task<(string Status, int Attempts)> StatusAsync(long inboxId) =>
        (await sql.QueryAsync<(string, int)>("SELECT status, attempt_count FROM dbo.inbox WHERE id = @inboxId", new { inboxId }))[0];

    private static string ErpOrder(string docNo, params (string Sku, int Qty)[] lines) =>
        $$"""{"DocNo":"{{docNo}}","DocDate":"14.11.2026","Lines":[{{string.Join(",", lines.Select(l => $$"""{"ItemNo":"{{l.Sku}}","Qty":{{l.Qty}},"Uom":"EA"}"""))}}]}""";

    private sealed class AlwaysFailingWriter : IWmsWriter
    {
        public Task<bool> AcceptOrderAsync(InboxMessage message, CanonicalOrder order, CancellationToken ct) => throw new InvalidOperationException("WMS is down");

        public Task<bool> ReceiveAsnAsync(InboxMessage message, CanonicalAsn asn, CancellationToken ct) => throw new InvalidOperationException("WMS is down");
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public ConcurrentBag<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
