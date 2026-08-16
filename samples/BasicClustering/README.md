# Basic Clustering

This sample starts two Orleans silos, joins them through Redis cluster membership,
and calls a grain after both silos are active. It focuses only on the step between
running Orleans in one process and deploying a production cluster.

## What the sample demonstrates

- `Aspire.Hosting.Orleans` supplies each replica with a unique silo endpoint and
  shared cluster identity.
- Redis provides cluster membership and gateway discovery.
- `.WithReplicas(2)` starts two instances of the same silo project.
- Orleans routes both callers to the same grain activation, regardless of which
  silo hosts it.

`UseLocalhostClustering()` and `UseDevelopmentClustering()` are the modern
in-memory equivalents of the former `MembershipTableGrain` approach. They are
development-only and require manually coordinating endpoints when multiple
processes run on one machine. This sample instead uses the same external
membership-provider model as a production cluster while Aspire supplies a local
Redis container and process-specific endpoints.

## Run the sample

Install the .NET 10 SDK, the Aspire CLI, and a Docker-compatible container
runtime. From this directory, run:

```powershell
aspire run --apphost BasicClustering.AppHost/BasicClustering.AppHost.csproj
```

In the Aspire dashboard, open the structured logs for either `silo` replica.
After both replicas join, each reports two active silos and a response like:

```text
The two-silo cluster is ready. Hello from the cluster. Grain 0 is running on S10.0.0.1:11111:...
```

Stop either replica in the dashboard to see the remaining silo observe the
membership change. Restart it to return to a two-silo cluster.

## Production guidance

The Redis container created by the AppHost is for local development. In
production, configure a secured, highly available managed Redis service or
another supported clustering provider, keep cluster and service IDs stable
within an environment, and run silo replicas across failure domains.
