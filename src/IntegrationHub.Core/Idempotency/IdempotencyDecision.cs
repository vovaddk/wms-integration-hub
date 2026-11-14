using System.Security.Cryptography;

namespace IntegrationHub.Core.Idempotency;

public enum IntakeOutcome
{
    /// <summary>First time this key was seen: message staged in the inbox.</summary>
    Accepted,

    /// <summary>Same key, same payload: a client retry. Return the original result, stage nothing.</summary>
    Replayed,

    /// <summary>Same key, different payload: a client bug. Reject (HTTP 422).</summary>
    Conflict,
}

public sealed record IntakeResult(IntakeOutcome Outcome, long InboxId);

public static class IdempotencyDecision
{
    /// <summary>SHA-256 over the raw request bytes. Byte-exact on purpose: a retry must resend the same bytes.</summary>
    public static byte[] HashPayload(ReadOnlySpan<byte> payload) => SHA256.HashData(payload);

    /// <summary>Decides what to do when the idempotency key already exists.</summary>
    public static IntakeOutcome ForExistingKey(
        string storedMessageType, ReadOnlySpan<byte> storedHash, string messageType, ReadOnlySpan<byte> payloadHash) =>
        storedMessageType == messageType && storedHash.SequenceEqual(payloadHash)
            ? IntakeOutcome.Replayed
            : IntakeOutcome.Conflict;
}
