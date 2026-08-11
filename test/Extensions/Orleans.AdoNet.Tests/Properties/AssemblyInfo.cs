using Xunit;

// ADO.NET tests recreate shared provider databases and cannot safely run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
