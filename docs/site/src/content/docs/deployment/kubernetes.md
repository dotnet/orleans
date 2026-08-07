---
title: Host Orleans on Kubernetes
description: Configure networking, lifecycle, probes, and rollouts for an Orleans cluster on Kubernetes.
ms.date: 08/02/2026
ms.topic: how-to
ms.custom: devops
---

# Host Orleans on Kubernetes

Kubernetes can host Orleans when pods have direct network connectivity and the application uses a production clustering provider. Orleans doesn't require a Kubernetes-specific hosting package. The recommended approach is to configure each silo explicitly from the pod name and pod IP supplied by the Kubernetes [downward API](https://kubernetes.io/docs/concepts/workloads/pods/downward-api/).

## Configure the silo

Reference `Microsoft.Orleans.Server` and a production clustering provider package. Configure the pod IP as the advertised address, listen on all pod interfaces, and use the pod name as the silo name:

```csharp
using System.Net;

var builder = WebApplication.CreateBuilder(args);

var podName = builder.Configuration["POD_NAME"]
    ?? throw new InvalidOperationException("POD_NAME isn't configured.");
var podIp = IPAddress.Parse(
    builder.Configuration["POD_IP"]
        ?? throw new InvalidOperationException("POD_IP isn't configured."));

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        // Configure one production clustering provider here.
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = builder.Configuration["ORLEANS_SERVICE_ID"]
                ?? throw new InvalidOperationException("ORLEANS_SERVICE_ID isn't configured.");
            options.ClusterId = builder.Configuration["ORLEANS_CLUSTER_ID"]
                ?? throw new InvalidOperationException("ORLEANS_CLUSTER_ID isn't configured.");
        })
        .Configure<SiloOptions>(options => options.SiloName = podName)
        .ConfigureEndpoints(
            advertisedIP: podIp,
            siloPort: 11_111,
            gatewayPort: 30_000,
            listenOnAnyHostAddress: true);
});

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(120);
});

var app = builder.Build();

// Map application-owned startup, readiness, and liveness endpoints.

app.Run();
```

All silos and clients must use the same service ID, cluster ID, and clustering provider. The example explicitly uses silo port `11111` and gateway port `30000`.

## Apply a production baseline

The following baseline intentionally omits the clustering provider credentials and application ingress. Supply those resources using workload identity and provider-specific configuration.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: dictionary-app
spec:
  replicas: 3
  minReadySeconds: 30
  strategy:
    type: RollingUpdate
    rollingUpdate:
      maxUnavailable: 0
      maxSurge: 1
  selector:
    matchLabels:
      app.kubernetes.io/name: dictionary-app
  template:
    metadata:
      labels:
        app.kubernetes.io/name: dictionary-app
    spec:
      automountServiceAccountToken: false
      terminationGracePeriodSeconds: 150
      containers:
        - name: app
          image: registry.example.com/dictionary-app:10.0.0
          imagePullPolicy: IfNotPresent
          ports:
            - name: http
              containerPort: 8080
              protocol: TCP
            - name: silo
              containerPort: 11111
              protocol: TCP
            - name: gateway
              containerPort: 30000
              protocol: TCP
          env:
            - name: POD_NAME
              valueFrom:
                fieldRef:
                  fieldPath: metadata.name
            - name: POD_IP
              valueFrom:
                fieldRef:
                  fieldPath: status.podIP
            - name: ORLEANS_SERVICE_ID
              value: dictionary-app
            - name: ORLEANS_CLUSTER_ID
              value: production
          startupProbe:
            httpGet:
              path: /health/startup
              port: http
            periodSeconds: 5
            timeoutSeconds: 2
            failureThreshold: 60
          readinessProbe:
            httpGet:
              path: /health/ready
              port: http
            periodSeconds: 5
            timeoutSeconds: 2
            failureThreshold: 2
          livenessProbe:
            httpGet:
              path: /health/live
              port: http
            periodSeconds: 10
            timeoutSeconds: 2
            failureThreshold: 3
          resources:
            requests:
              cpu: 500m
              memory: 512Mi
            limits:
              memory: 1Gi
---
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: dictionary-app
spec:
  minAvailable: 2
  selector:
    matchLabels:
      app.kubernetes.io/name: dictionary-app
```

Apply the manifest in a namespace dedicated or appropriately shared for the application:

```bash
kubectl apply --namespace <namespace> --filename orleans.yaml
```

The baseline disables [service account token mounting](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/) because Orleans doesn't need to call the Kubernetes API. Add a service account and permissions only for application features that require them.

## Deploy with Aspire

[Aspire](../host/aspire-integration.md) can model an Orleans application and its backing resources in an AppHost. The `Aspire.Hosting.Kubernetes` integration can publish that application model as a Helm chart or deploy it to the Kubernetes cluster selected by the current `kubectl` context.

Aspire doesn't change the Orleans networking and lifecycle requirements described on this page. Before deploying generated resources:

- Configure a production clustering provider and durable state providers in the Orleans resource model.
- Run multiple silo replicas across failure domains.
- Ensure each silo receives its own pod name and pod IP and advertises that pod IP.
- Expose the silo and gateway container ports to the required pod and client networks.
- Add application-owned startup, readiness, and liveness probes.
- Set resource requests, disruption budgets, rollout policy, and termination grace periods from measured behavior.

Review the generated Helm chart before applying it. Use the Kubernetes integration's resource customization APIs when the generated workload doesn't include these Orleans-specific requirements.

For the supported APIs and deployment commands, see [Orleans and Aspire integration](../host/aspire-integration.md), [Aspire Kubernetes integration](https://aspire.dev/integrations/compute/kubernetes/), and [Deploy with Aspire](https://aspire.dev/deployment/deploy-with-aspire/).

## Optional Kubernetes hosting package

The [`Microsoft.Orleans.Hosting.Kubernetes`](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.Kubernetes) package is optional and isn't generally recommended. Consider it only for a simple topology where exactly one Kubernetes `Deployment` object owns exactly one Orleans cluster.

> [!IMPORTANT]
> Don't use the package for an Orleans cluster composed from multiple `Deployment` objects, StatefulSets, custom controllers, or blue-green or canary workloads sharing a cluster identity. Configure endpoints explicitly instead.

For the supported simple topology, <xref:Orleans.Hosting.KubernetesHostingExtensions.UseKubernetesHosting*> configures the silo name, pod IP, listening endpoints, service ID, and cluster ID from pod metadata. It also calls the Kubernetes API to reconcile Orleans membership with matching pods. It supplements a production clustering provider; it doesn't replace one.

To use it:

1. Reference `Microsoft.Orleans.Hosting.Kubernetes` and call `UseKubernetesHosting()`.
1. Add `orleans/serviceId` and `orleans/clusterId` labels to the `Deployment` pod template.
1. Supply `POD_NAME`, `POD_NAMESPACE`, `POD_IP`, `ORLEANS_SERVICE_ID`, and `ORLEANS_CLUSTER_ID` through the downward API.
1. Mount a dedicated service account token and grant this namespace-scoped role:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: orleans-hosting
rules:
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["get", "list", "watch", "patch"]
```

Bind the role to the workload's dedicated service account. If you explicitly enable `KubernetesHostingOptions.DeleteDefunctSiloPods`, also grant `delete`. Keep that option disabled unless its operational consequences have been reviewed.

## Network requirements

Allow direct pod-IP TCP traffic:

- Every silo pod to port `11111` on every silo pod.
- Every Orleans client to port `30000` on every silo pod.
- Every silo and client to the clustering provider.
- Silo pods to the Kubernetes API only when the optional hosting package is enabled.

Don't place a Kubernetes `Service` virtual IP in `AdvertisedIPAddress`. Orleans advertises each pod IP so peers can contact that specific silo. A `Service` can expose application HTTP ingress, but it doesn't replace Orleans membership or direct silo connectivity.

If a service mesh intercepts TCP, validate long-lived connections, pod-address preservation, mutual TLS policy, shutdown ordering, and retries. Exclude Orleans ports from interception if the mesh can't preserve the required semantics.

## Health probes

The three paths in the manifest are [startup, readiness, and liveness probes](https://kubernetes.io/docs/tasks/configure-pod-container/configure-liveness-readiness-startup-probes/) that the application must implement according to [Health and observability](health-and-observability.md):

- Startup succeeds after the silo joins the cluster and required initialization completes.
- Readiness becomes false before shutdown and whenever the application can't safely accept new traffic.
- Liveness performs only a local forward-progress check and doesn't call grains or dependencies.

The five-minute startup allowance is an example, not a universal default. Set it from measured startup and recovery time. Aggressive probes can turn provider latency or CPU pressure into a cluster-wide restart loop.

## Resource requests and limits

Set [container resource requests and limits](https://kubernetes.io/docs/concepts/configuration/manage-resources-containers/) from measurements under representative load, including a silo loss and rolling deployment. Kubernetes uses requests for scheduling and uses limits to constrain a running container; they aren't interchangeable capacity settings.

- **CPU requests** reserve scheduling capacity. If requests are too low, Kubernetes can place too many silos on one node and create contention during bursts or failover.
- **CPU limits** can throttle the .NET process even when the node has spare CPU. Tail latency, membership probes, garbage collection, and activation recovery can all suffer under throttling. The baseline intentionally specifies a CPU request without a CPU limit.
- **Memory requests** should cover a representative working set, including activations, caches, serialization buffers, and expected failover growth.
- **Memory limits** are enforced by terminating the container when it exceeds the cgroup limit. Leave headroom for bursts and failover; don't rely on a managed <xref:System.OutOfMemoryException> or graceful shutdown.

Monitor CPU throttling, working set, allocation rate, garbage collection, scheduler delay, activation count, and pod restarts. Revisit resource settings when grain state, placement, traffic, runtime, or node sizes change.

Namespace [`ResourceQuota`](https://kubernetes.io/docs/concepts/policy/resource-quotas/) and [`LimitRange`](https://kubernetes.io/docs/concepts/policy/limit-range/) policies can reject pods or inject defaults. Verify the effective pod specification after admission rather than assuming the submitted manifest is what runs.

For workload sizing, overload protection, and scaling signals, see [Capacity planning and scaling](capacity-planning.md).

## Shutdown, rollouts, and scaling

Kubernetes sends `SIGTERM` and begins the pod termination grace period. The .NET host must observe the signal, report not ready, and receive enough time to stop Orleans gracefully. In the example, the 150-second pod grace period is longer than the 120-second host shutdown timeout.

Keep `maxUnavailable: 0` and some surge capacity when the cluster can't tolerate losing a ready silo during rollout. The PodDisruptionBudget protects against voluntary disruptions, not node failure.

Scale in gradually. Verify that membership stabilizes and remaining silos have capacity before removing more pods. See [Graceful shutdown and upgrades](upgrades.md).

## Troubleshoot Kubernetes hosting

For explicit endpoint configuration, compare `POD_NAME`, `POD_IP`, service ID, cluster ID, and the advertised membership endpoint.

If the optional hosting package reports missing `KUBERNETES_SERVICE_HOST` or `KUBERNETES_SERVICE_PORT`, verify that the process is running in a pod and that service environment links haven't been disabled. If the API returns `403 Forbidden`, check the pod's service account, role binding namespace, and required pod verbs.

For broader triage, see [Troubleshoot deployments](troubleshooting-deployments.md).
