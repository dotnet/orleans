---
title: Service Fabric hosting guidance retired
description: Redirect from retired Orleans Service Fabric hosting guidance to current platform requirements.
ms.date: 08/02/2026
ms.topic: reference
---

# Service Fabric hosting guidance retired

The Orleans Service Fabric sample and hosting guidance on this URL are retired. They relied on old project formats and integration code that isn't maintained or validated for Orleans 10.

Don't use the removed snippets as a deployment baseline. Existing Service Fabric applications should apply the generic [platform requirements](platform-guides.md), including direct per-instance TCP connectivity, explicit advertised endpoints, an external clustering provider, health checks, graceful shutdown, and controlled upgrades.

For a maintained container-platform guide, see [Host Orleans on Kubernetes](kubernetes.md).
