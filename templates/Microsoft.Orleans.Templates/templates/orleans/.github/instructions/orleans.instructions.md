---
applyTo: "**"
---

# Microsoft Orleans guidance

Use the [Microsoft Orleans documentation](https://dotnet.github.io/orleans/) as the primary reference when designing or changing Orleans applications. Use the conceptual and task-oriented guidance for application architecture, hosting, grains, persistence, streaming, deployment, and operations. Use the [C# API reference](https://dotnet.github.io/orleans/docs/api/csharp/) for exact public contracts.

Match guidance and APIs to the `Microsoft.Orleans.*` package versions referenced by this repository. Preserve existing `[Id(n)]` serializer field identifiers, and add `[GenerateSerializer]` to application types that cross grain calls, persistence, or streams. Keep the generated local development topology, including its localhost providers or local emulators such as Azurite. Configure production clustering, storage, reminders, and observability using the hosting and deployment guidance.
