# Microsoft Orleans contract tool

The `orleans-contracts` .NET tool regenerates `OrleansContracts.txt` manifests for projects which enable the Orleans contract compatibility analyzer.

Install it in a repository tool manifest:

```console
dotnet new tool-manifest
dotnet tool install Microsoft.Orleans.ContractTool
```

Regenerate one project or every enabled project in a solution:

```console
dotnet tool run orleans-contracts PATH_TO_PROJECT_OR_SOLUTION
```

For solution input, the tool evaluates the project graph, selects projects where `EnableOrleansContractsAnalyzer` is `true`, and runs the analyzer fixes against a temporary filtered solution.
