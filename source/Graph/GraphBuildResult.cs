using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Graph;

public sealed record GraphBuildResult(DataFlowGraph Graph, IReadOnlyList<Diagnostic> Diagnostics);
