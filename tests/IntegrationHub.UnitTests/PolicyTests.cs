using System.Text;
using IntegrationHub.Core.Idempotency;
using IntegrationHub.Core.Processing;
using IntegrationHub.Core.Security;

namespace IntegrationHub.UnitTests;

public class IdempotencyDecisionTests
{
    private static readonly byte[] Hash = IdempotencyDecision.HashPayload("""{"a":1}"""u8);

    [Fact]
    public void Same_type_and_same_payload_is_a_replay() =>
        IdempotencyDecision.ForExistingKey("order", Hash, "order", IdempotencyDecision.HashPayload("""{"a":1}"""u8))
            .ShouldBe(IntakeOutcome.Replayed);

    [Fact]
    public void Different_payload_is_a_conflict() =>
        IdempotencyDecision.ForExistingKey("order", Hash, "order", IdempotencyDecision.HashPayload("""{"a":2}"""u8))
            .ShouldBe(IntakeOutcome.Conflict);

    [Fact]
    public void Same_payload_on_a_different_endpoint_is_a_conflict() =>
        IdempotencyDecision.ForExistingKey("order", Hash, "asn", Hash).ShouldBe(IntakeOutcome.Conflict);

    [Fact]
    public void Hash_is_byte_exact_so_whitespace_changes_count_as_a_different_payload() =>
        IdempotencyDecision.HashPayload("""{"a": 1}"""u8).ShouldNotBe(Hash);
}

public class RetryPolicyTests
{
    private readonly RetryPolicy _policy = new() { MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromMinutes(5) };

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    public void Delay_doubles_per_attempt_and_jitter_stays_within_half_to_full(int attempt, int fullSeconds)
    {
        _policy.NextDelay(attempt, 0.0).ShouldBe(TimeSpan.FromSeconds(fullSeconds / 2.0));
        _policy.NextDelay(attempt, 0.999999).TotalSeconds.ShouldBe(fullSeconds, tolerance: 0.001);
    }

    [Fact]
    public void Delay_is_capped_at_max_delay_even_for_huge_attempt_counts()
    {
        _policy.NextDelay(50, 1.0).ShouldBe(TimeSpan.FromMinutes(5));
        _policy.NextDelay(5000, 1.0).ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void Dead_letters_after_max_attempts(int attempts, bool expected) =>
        _policy.ShouldDeadLetter(attempts).ShouldBe(expected);
}

public class HmacSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"order_id":"1"}""");

    [Fact]
    public void Accepts_signature_computed_with_the_same_secret() =>
        HmacSignature.IsValid(Body, HmacSignature.Compute(Body, "secret"), "secret").ShouldBeTrue();

    [Fact]
    public void Accepts_upper_case_hex() =>
        HmacSignature.IsValid(Body, "sha256=" + HmacSignature.Compute(Body, "secret")[7..].ToUpperInvariant(), "secret").ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=nothex")]
    [InlineData("md5=abcd")]
    [InlineData("sha256=00")]
    public void Rejects_missing_or_malformed_headers(string? header) =>
        HmacSignature.IsValid(Body, header, "secret").ShouldBeFalse();

    [Fact]
    public void Rejects_wrong_secret_and_tampered_body()
    {
        var signature = HmacSignature.Compute(Body, "secret");
        HmacSignature.IsValid(Body, signature, "other").ShouldBeFalse();
        HmacSignature.IsValid(Encoding.UTF8.GetBytes("""{"order_id":"2"}"""), signature, "secret").ShouldBeFalse();
    }
}
