using System.Net;
using System.Net.Http.Json;
using System.Text;
using IntegrationHub.Api;
using IntegrationHub.Core.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationHub.IntegrationTests;

public sealed class InboundWebhookTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string ErpOrder = """{"DocNo":"SO-1","DocDate":"14.11.2026","Lines":[{"ItemNo":"SKU-1","Qty":1,"Uom":"EA"}]}""";
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await sql.ResetAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); // shop has an HMAC secret configured there
            b.UseSetting("ConnectionStrings:WmsIntegrationHub", sql.ConnectionString);
            b.UseSetting("Database:MigrateOnStartup", "false");
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Same_key_and_same_payload_returns_original_result_without_a_duplicate()
    {
        var first = await PostOrderAsync("erp", "key-1", ErpOrder);
        var second = await PostOrderAsync("erp", "key-1", ErpOrder);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await second.Content.ReadFromJsonAsync<IntakeResponse>(Ct))!.InboxId
            .ShouldBe((await first.Content.ReadFromJsonAsync<IntakeResponse>(Ct))!.InboxId);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbox")).ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_rejected_with_422()
    {
        (await PostOrderAsync("erp", "key-2", ErpOrder)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var conflict = await PostOrderAsync("erp", "key-2", ErpOrder.Replace("SO-1", "SO-2", StringComparison.Ordinal));

        conflict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbox")).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_duplicates_are_resolved_by_the_unique_key_not_a_pre_check()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PostOrderAsync("erp", "race-key", ErpOrder)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(11);
        (await sql.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbox")).ShouldBe(1);
    }

    [Theory]
    [InlineData("erp", null, HttpStatusCode.BadRequest)]
    [InlineData("crm", "k", HttpStatusCode.BadRequest)]
    [InlineData(null, "k", HttpStatusCode.BadRequest)]
    public async Task Missing_key_or_unknown_source_is_rejected(string? source, string? key, HttpStatusCode expected) =>
        (await PostOrderAsync(source, key, ErpOrder)).StatusCode.ShouldBe(expected);

    [Fact]
    public async Task Body_that_is_not_json_is_rejected() =>
        (await PostOrderAsync("erp", "k-json", "<order/>")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Shop_requests_need_a_valid_hmac_signature()
    {
        const string body = """{"order_id":"WEB-1","created_at":"2026-11-14T10:00:00Z","items":[{"sku":"SKU-1","quantity":1,"unit":"pcs"}]}""";

        (await PostOrderAsync("shop", "s-1", body, signature: "sha256=" + new string('0', 64))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostOrderAsync("shop", "s-1", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostOrderAsync("shop", "s-1", body, HmacSignature.Compute(Encoding.UTF8.GetBytes(body), "dev-only-shop-secret")))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Correlation_id_from_the_caller_is_stored_on_the_inbox_row()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inbound/orders") { Content = new StringContent(ErpOrder, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Source-System", "erp");
        request.Headers.Add("Idempotency-Key", "corr-key");
        request.Headers.Add("X-Correlation-Id", "trace-abc-123");

        var response = await _client.SendAsync(request, Ct);

        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["trace-abc-123"]);
        (await sql.ScalarAsync<string>("SELECT correlation_id FROM dbo.inbox")).ShouldBe("trace-abc-123");
    }

    [Fact]
    public async Task Health_endpoint_checks_the_database() =>
        (await _client.GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

    private Task<HttpResponseMessage> PostOrderAsync(string? source, string? key, string body, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inbound/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (source is not null)
        {
            request.Headers.Add("X-Source-System", source);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (signature is not null)
        {
            request.Headers.Add("X-Signature", signature);
        }

        return _client.SendAsync(request, Ct);
    }
}
