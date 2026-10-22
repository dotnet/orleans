using Xunit.Sdk;
using Xunit.v3;

// Run tests sequentially while they recreate shared provider databases.
[assembly: Parallelization(Mode = ParallelMode.None)]
