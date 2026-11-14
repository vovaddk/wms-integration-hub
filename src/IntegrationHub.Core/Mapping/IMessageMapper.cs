using IntegrationHub.Core.Canonical;

namespace IntegrationHub.Core.Mapping;

/// <summary>
/// Strategy: one implementation per external system translates that system's payloads into the canonical model.
/// Throws <see cref="MappingException"/> when a payload can never be mapped (permanent failure, no retry).
/// </summary>
public interface IMessageMapper
{
    string SourceSystem { get; }

    CanonicalOrder MapOrder(string payload);

    CanonicalAsn MapAsn(string payload);
}

/// <summary>The payload is structurally wrong for its source. Retrying will not help, so it is dead-lettered at once.</summary>
public sealed class MappingException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class MapperRegistry(IEnumerable<IMessageMapper> mappers)
{
    private readonly Dictionary<string, IMessageMapper> _bySource =
        mappers.ToDictionary(m => m.SourceSystem, StringComparer.OrdinalIgnoreCase);

    public IMessageMapper Get(string sourceSystem) =>
        _bySource.TryGetValue(sourceSystem, out var mapper)
            ? mapper
            : throw new MappingException($"No mapper registered for source system '{sourceSystem}'.");
}
