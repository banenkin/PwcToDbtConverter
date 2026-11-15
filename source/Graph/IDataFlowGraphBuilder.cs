using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Graph;

/// <summary>
/// Builds the instance graph for one mapping. Broken connectors are reported and dropped.
/// </summary>
public interface IDataFlowGraphBuilder
{
    GraphBuildResult Build(MappingDef mapping, PowerMartDocument document);
}
