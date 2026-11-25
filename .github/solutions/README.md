# CI test graphs

`CodeGenerator.slnf` selects the CodeGen partition's test roots:
`Orleans.CodeGenerator.Tests` and `Orleans.DefaultCluster.Tests`. The existing
`Provider`, `Area`, and `Suite` filter selects the tests within those assemblies.
Project references supply the transitive build graph, including runtime assets,
F# assemblies, and both separately published versions of `TestVersionGrains`.

The test actions use the selected solution for restore, build, and discovery on
every attempt, with the same coverage artifact identity. Other partitions use
`Orleans.slnx`. The full-solution CI build and package compatibility validation
cover the shipping graph. After building, CI compares full and focused CodeGen
discovery by test ID, name, assembly, target framework, architecture, and reported
traits for both target frameworks.

When adding CodeGen tests in another project, include that project in this filter
and run `.github/scripts/test-codegenerator-graph.ps1` for each target framework
after a full build.
