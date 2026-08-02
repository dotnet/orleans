---
title: Consul clustering provider
description: Find current guidance for selecting and configuring Consul as an Orleans 10 clustering provider.
ms.date: 08/02/2026
ms.topic: reference
---

# Consul clustering provider

Consul is a clustering provider, not a deployment platform. Reference [`Microsoft.Orleans.Clustering.Consul`](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.Consul) and configure the same Consul address, service ID, and cluster ID for every silo and client.

See [Choose a clustering provider](networking.md#choose-a-clustering-provider) for production selection criteria and [Typical configurations](../host/configuration-guide/typical-configurations.md) for the current Orleans hosting model.

Operate Consul as a secured, highly available dependency by following the [Consul documentation](https://developer.hashicorp.com/consul/docs). Don't use the former single-node development command as a production configuration.
