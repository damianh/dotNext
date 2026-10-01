using System.Diagnostics.CodeAnalysis;

namespace DotNext;

[ExcludeFromCodeCoverage]
internal static class TestCollections
{
    internal const string Raft = "Raft";

    internal const string AsyncPrimitives = "Async";

    internal const string AdvancedSynchronization = "AdvancedAsync";

    internal const string WriteAheadLog = "WAL";

    // GC.GetTotalAllocatedBytes is process-wide, so allocation budgets are measured without parallel tests
    internal const string AllocationBudget = "AllocationBudget";
}

[ExcludeFromCodeCoverage]
[CollectionDefinition(TestCollections.AllocationBudget, DisableParallelization = true)]
public sealed class AllocationBudgetCollection;