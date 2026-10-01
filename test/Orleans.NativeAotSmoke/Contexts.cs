using Orleans.Serialization.ContextSmoke;


ContextContracts.NestedCollectionsRoundTripAndCopy();
ContextContracts.GeneratedModelsTraverseDependencies();
ContextContracts.ReferencedGeneratedModelsTraverseDependencies();
ContextContracts.NullableArrayAndEnumRoundTrip();
ContextContracts.RecursiveModelsPreserveIdentity();
ContextContracts.MutualRecursionPreservesIdentity();
ContextContracts.CollectionRecursionPreservesIdentity();
ContextContracts.DuplicateContextsAndConcurrentResolution();
ContextContracts.MissingTypesAndCustomComparersFailClearly();
ContextContracts.ModelAliasesAndTypeIdsRoundTrip();
StaticFactoryContracts.CyclicConstructionPublishesCompletedGraphs();
StaticFactoryContracts.FailedCyclicConstructionRollsBack();
StaticFactoryContracts.CaughtNestedFailureFaultsTheWholeGraph();
Console.WriteLine("Serializer context contracts passed: nested collections, generated models, value types, cycles, identity, concurrency, and diagnostics.");
