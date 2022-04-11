# Contributing to Orleans

Some notes and guidelines for developers who want to contribute to Orleans.

## Contributing to this project

Here are some pointers for anyone looking for mini-features and work items that would make a positive contribution to Orleans.

These are just a few ideas, so if you think of something else that would be useful, then spin up a [discussion thread](https://github.com/dotnet/orleans/issues) on GitHub to discuss the proposal, and go for it.

* **[Orleans GitHub Repository](https://github.com/dotnet/orleans)**

Pull requests are always welcome.

* **[Intern and Student Projects](https://docs.microsoft.com/dotnet/orleans/resources/student-projects)**

Some suggestions for possible intern / student projects.

* **[Documentation Guidelines](https://docs.microsoft.com/contribute/dotnet/dotnet-contribute)**

A style guide for writing documentation for this site.

## Code contributions

This project uses the same contribution process as the other **[.NET projects](https://github.com/dotnet)** on GitHub.

* **[.NET Project Contribution Guidelines](https://github.com/dotnet/runtime/blob/main/CONTRIBUTING.md)**

Guidelines and workflow for contributing to .NET projects on GitHub.

* **[.NET CLA](https://cla.dotnetfoundation.org/)**

Contribution License Agreement for .NET projects on GitHub.

* **[.NET Framework Design Guidelines](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/framework-design-guidelines-digest.md)**

Some basic API design rules, coding standards, and style guide for .NET Framework APIs.

## Coding Standards and Conventions

We try not to be too OCD about coding style wars, but in case of disputes we do fall back to the core principles in the two ".NET Coding Standards" books used by the other .NET OSS projects on GitHub:

* [C# Coding Style Guide](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)

* [.NET Framework Design Guidelines](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/framework-design-guidelines-digest.md)

There are lots of other useful documents on the [.NET](https://github.com/dotnet/runtime/tree/main/docs#coding-guidelines) documentation sites which are worth reading, although most experienced C# developers will probably have picked up many of those best-practices by osmosis, particularly around performance and memory management.

## Source code organization

Orleans has not religiously followed a "One Class Per File" rule, but instead we have tried to use pragmatic judgment to maximize the change of "code understand-ability" for developers on the team. If lots of small-ish classes share a "common theme" and/or are always dealt with together, then it is OK to place those into one source code file in most cases. See for example the various "log consumer" classes were originally placed in single source file, as they represented a single unit of code comprehension.

As a corollary, it is much easier to find the source code for a class if it is in a file with the same name as the class [similar to Java file naming rules], so there is a tension and value judgment here between code find-ability and minimizing / constraining the number of projects in a solution and files within a project [which both have direct impact on the Visual Studio "Opening" and "Building" times for large projects].

Code search tools in VS and ReSharper definitely help here.

## Dependencies and Inter-Project References

One topic that we are very strict about is around dependency references between components and sub-systems.

### Component / Project References

References between projects in a solution must always use "**Project References**" rather than "_DLL References_" to ensure that component build relationships are known to the build tools.

**Right**:

```xml
<ProjectReference Include="..\Orleans\Orleans.csproj">
    <Project>{BC1BD60C-E7D8-4452-A21C-290AEC8E2E74}</Project>
    <Name>Orleans</Name>
</ProjectReference>
```

_Wrong_:

```xml
<Reference Include="Orleans" >
    <HintPath>..\Orleans\bin\Debug\Orleans.dll</HintPath>
</Reference>
```

In order to help ensure we keep inter-project references clean, then on the build servers [and local `Build.cmd` script] we deliberately use side-by-side input `.\src` and output `.\Binaries` directories rather than the more normal in-place build directory structure (eg. `[PROJ]\bin\Release`) used by VS on local dev machines.

### Unified component versions

We use the same unified versions of external component throughout the Orleans code base, and so should never need to add `bindingRedirect` entries in `App.config` files.

Also, in general it should almost never be necessary to have `Private=True` elements in Orleans project files, except to override a conflict with a Windows / VS "system" component.
Some package management tools can occasionally get confused when making version changes, and sometimes think that we are using multiple versions of the same assembly within a solution, which of course we never do.

We long for the day when package management tools for .NET can make version changes transactionally. Until then, it is occasionally necessary to "fix" the misguided actions of some .NET package management tools by hand-editing the .csproj files (they are just XML text files) back to sanity and/or using the "Discard Edited Line" functions that most good Git tools such as [Atlassian SourceTree](https://www.sourcetreeapp.com/) provide.

Using "sort" references and unified component versions avoids creating brittle links between Orleans run-time and/or external components, and has proved highly effective in the last several years at reducing stress levels for the Orleans Team during important deployment milestones. :)
