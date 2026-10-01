using Orleans.Serialization.ContextSmoke;

StaticFactoryContracts.CyclicConstructionPublishesCompletedGraphs();
StaticFactoryContracts.FailedCyclicConstructionRollsBack();
StaticFactoryContracts.CaughtNestedFailureFaultsTheWholeGraph();
StaticFactoryContracts.DirectProviderServicesRemainGraphOwned();
Console.WriteLine("Static serializer factory contracts passed: atomic graph publication and constructor-failure rollback.");
