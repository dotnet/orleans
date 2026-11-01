using Xunit.v3;

[assembly: Parallelization(MaxThreads = -1)]
[assembly: TestArea("Analyzer")]
[assembly: TestProvider("None")]
[assembly: TestSuite("BVT")]
