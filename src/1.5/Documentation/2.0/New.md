---
layout: page
title: What's new in Orleans 2.0
---

# What's new in Orleans 2.0

2.0 is a major release of Orleans with the main goal of making it .NET Standard 2.0 compatible and cross-platform (via .NET Core). As part of that effort, several modernizations of Orleans APIs were made to make it more aligned with how technologies like ASP.NET are configured and hosted today.

Being compatible with .NET Standard 2.0, Orleans 2.0 can be used by applications targeting .NET Core or full .NET Framework. The emphasis of testing by the Core team for this release is on full .NET Framework to ensure that existing applications can easily migrate from 1.5 to 2.0, and with full backward compatibility.

The open source community has been running pre-release version of 2.0 with .NET Core successfully on both Windows and Linux, but test coverage on those platforms at this moment is much less comprehensive than on full .NET Framework. We plan to expand testing on .NET Core after the 2.0 release.

The most significant changes in 2.0 are as follows.

* Completely move to programmatic config leveraging Dependency Injection with a fluid builder pattern API. Old API based on configuration objects and XML files is preserved for backward compatibility, but will not move forward, and will get deprecated in the future. See more details in the [Configuration](Configuration2.0.md) section.

* Explicit programmatic specification of application assemblies that replaces automatic scanning of folders by the Orleans runtime upon silo or client initialization. Orleans will still automatically find relevant types, such as grain interfaces and classes, serializers, etc. in the specified assemblies, but it will not anymore try to load every assembly it can find in the folder. An optional helper method for loading all assemblies in the folder is provided for backward compatibility. See [Configuration](Configuration2.0.md) and [Migration](Migration1.5.md) sections for more details.

* Overhaul of code generation. While mostly invisible for developer, code generation became much more robust in handling serialization of various possible types. Special handling is required for F# assemblies. See [Code generation](Codegen.md) section for more details.

* Created a `Microsoft.Orleans.Core.Abstractions` NuGet package and moved/refactored several types into it. Grain code would most likely need to reference only these abstractions, whereas the silo host and client will reference more of Orleans packages. We plan to update this package less frequently.

* Add support for Scoped services. This means that each grain activation gets its own scoped service provider, and Orleans registers a contextual `IGrainActivationContext` object that can be injected into a *Transient* or *Scoped* service to get access to activation specific information and grain activation lifecycle events. This is similar to how ASP.NET Core 2.0 creates a scoped context for each Request, but in the case of Orleans it applies to the entire lifetime of a grain activation. See [Service Lifetimes and Registration Options](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection#service-lifetimes-and-registration-options) in the ASP.NET Core documentation for more information about service lifetimes.

* Migrated the logging infrastructure to use `Microsoft.Extensions.Logging` (same abstractions as ASP.NET Core 2.0).

* 2.0 includes a beta version of support for ACID distributed cross-grain transactions. The functionality will be ready for prototyping and development, and will graduate for production use sometime after 2.0 release. See [Transactions](Transactions.md) for more details.
