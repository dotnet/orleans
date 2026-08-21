---
title: Host Orleans on Azure App Service
description: Choose and operate the supported Orleans topology on Windows or Linux Azure App Service.
ms.date: 08/21/2026
ms.topic: overview
ms.custom: devops
---

# Host Orleans on Azure App Service

Azure App Service can host Orleans by cohosting one silo with the application on every worker in a dedicated multi-instance plan. Each worker advertises its private instance address and a dynamically allocated private port. Azure Table Storage provides shared cluster membership and durable grain state in the maintained sample.

Choose the operating-system guide for the target plan:

| Target | Use this guide | Platform-specific behavior |
| --- | --- | --- |
| Windows App Service | [Deploy Orleans to Azure App Service on Windows](deploy-to-azure-app-service.md) | IIS integration, Windows paths and process model, and the Windows App Service Authentication module |
| Linux App Service | [Deploy Orleans to Azure App Service on Linux](deploy-to-azure-app-service-linux.md) | Built-in Linux .NET stack, container startup deadlines and paths, and the App Service Authentication ambassador sidecar |

Both guides use the same application, Bicep modules, staging-slot workflow, managed identity, health endpoints, and operational model.

## Supported topology

The maintained topology uses:

- A dedicated Premium v3 App Service plan with at least three workers in the maintained sample.
- One application process and one Orleans silo on every worker.
- `WEBSITE_PRIVATE_IP` and the first port in the comma-separated `WEBSITE_PRIVATE_PORTS` allocation as the advertised silo endpoint.
- A cohosted local Orleans client, with the Orleans gateway disabled.
- Azure Table Storage for clustering and grain state, authorized by managed identity.
- Separate production and staging cluster IDs with a shared stable service ID.

The App Service front end handles HTTP traffic. Orleans silo connections use the private per-worker endpoints. If trusted external Orleans clients are required, allocate and validate a second private port for the gateway and ensure every client network can route to every advertised gateway mapping.

## Qualify the target environment

App Service publishes the private instance settings used by the sample. The application validates their presence during startup and advertises the resulting endpoint.

Establish production evidence on the selected operating system, region, plan tier, networking configuration, and scale:

1. Scale to at least three workers.
1. Confirm every worker receives a distinct private address and the required private port count.
1. Match each active Orleans membership row to one worker instance.
1. Test bidirectional TCP connectivity among every advertised silo endpoint.
1. Repeat the checks during scale-out, scale-in, worker replacement, restart, and slot swap.

Regional virtual network integration supplies the private worker address and outbound virtual-network path. The allocated private ports establish the target-specific inbound silo path. Keep the integration subnets sized for planned scale, upgrades, and worker replacement.

App Service distributes multiple plan workers across platform fault domains. For an availability objective that includes zone failure, enable [App Service zone redundancy](https://learn.microsoft.com/azure/reliability/reliability-app-service) on a supported Premium plan and confirm that the selected region, scale unit, tier, and worker count meet the platform requirements. The maintained sample creates multiple Premium v3 workers but leaves zone redundancy as an environment-specific production adaptation.

## Production checklist

Use the detailed Windows or Linux guide to complete each outcome.

| Concern | App Service outcome |
| --- | --- |
| Topology and networking | Every worker advertises its private instance address and allocated silo port, and every worker can connect to every active membership endpoint. |
| Dependencies and data | Azure Table clustering and durable grain state use environment-specific tables. Applications configure reminder and stream providers explicitly when those features are used. |
| Identity and secrets | A user-assigned managed identity receives narrow data-plane roles. App Service Authentication and deployment identities remain separate, and secrets use protected settings or Key Vault references. |
| Health and lifecycle | Startup warm-up and `/health/ready` complete after Orleans starts. Readiness becomes unavailable before host shutdown, and the host uses the available App Service termination interval. |
| Scaling and resilience | The plan retains the tested minimum worker count and spare capacity. Fault-domain distribution and any required zone redundancy are documented. Scale, replacement, zone or worker loss, and dependency-failure behavior are validated under load. |
| Upgrades and rollback | Compatible releases warm in a staging slot and join the production cluster during swap. Incompatible releases use a separate app and cluster ID. |
| Observability and incidents | Application Insights, Log Analytics, Orleans telemetry, App Service instance metadata, membership evidence, and slot operations are correlated. |
| Infrastructure delivery | Bicep and the GitHub OIDC workflow create reproducible infrastructure and deploy immutable application artifacts through staging. |

Complete the [production-readiness checklist](production-readiness.md), [Topology, networking, and clustering](networking.md), [Health and observability](health-and-observability.md), [Capacity planning and scaling](capacity-planning.md), [Graceful shutdown and upgrades](upgrades.md), and [Troubleshoot deployments](troubleshooting-deployments.md) guidance for the target environment.
