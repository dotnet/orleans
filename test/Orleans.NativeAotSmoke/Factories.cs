using Orleans.Serialization.ContextSmoke;

StaticFactoryContracts.CyclicConstructionPublishesCompletedGraphs();
StaticFactoryContracts.FailedCyclicConstructionRollsBack();
StaticFactoryContracts.CaughtNestedFailureFaultsTheWholeGraph();
Console.WriteLine("Static serializer factory contracts passed: atomic graph publication and constructor-failure rollback.");
