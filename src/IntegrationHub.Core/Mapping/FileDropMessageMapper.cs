using System.Text.Json;
using IntegrationHub.Core.Canonical;

namespace IntegrationHub.Core.Mapping;

/// <summary>CSV orders are already parsed and validated into the canonical model before they reach the inbox.</summary>
public sealed class FileDropMessageMapper : IMessageMapper
{
    public const string Source = "file";

    public string SourceSystem => Source;

    public CanonicalOrder MapOrder(string payload) => MappingJson.Deserialize<CanonicalOrder>(payload, JsonSerializerOptions.Web);

    public CanonicalAsn MapAsn(string payload) =>
        throw new MappingException("ASNs are not imported from files.");
}
