---
title: Host Orleans on Kubernetes
description: Configure networking, RBAC, lifecycle, probes, and rollouts for an Orleans 10 cluster on Kubernetes.
ms.date: 08/02/2026
ms.topic: how-to
ms.custom: devops
---

# Host Orleans on Kubernetes

Kubernetes can host Orleans when pods have direct network connectivity and the application uses a production clustering provider. The `Microsoft.Orleans.Hosting.Kubernetes` package integrates pod identity and lifecycle information with Orleans; it doesn't use Kubernetes as the clustering provider.

## Configure the silo

Reference `Microsoft.Orleans.Server`, a clustering provider package, and `Microsoft.Orleans.Hosting.Kubernetes`. Configure the clustering provider and call <xref:Orleans.Hosting.KubernetesHostingExtensions.UseKubernetesHosting*>:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseKubernetesHosting()
        // Configure one production clustering provider here.
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "dictionary-app";
            options.ClusterId = "production";
        });
});

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(120);
});

var app = builder.Build();

// Map application-owned startup, readiness, and liveness endpoints.

app.Run();
```

Environment variables in the manifest can override the service and cluster IDs shown in code. All silos and clients must use the same values and clustering provider.

`UseKubernetesHosting`:

- Sets the silo name from `POD_NAME`.
- Advertises `POD_IP`.
- Listens on all local interfaces using the configured silo and gateway ports.
- Reads the service and cluster IDs from `ORLEANS_SERVICE_ID` and `ORLEANS_CLUSTER_ID`.
- Uses the Kubernetes API to reconcile Orleans members with pods carrying the same identity labels.

The default silo and gateway ports are `11111` and `30000`. Set them explicitly if the application uses different values.

## Apply a production baseline

The following baseline intentionally omits the clustering provider credentials and application ingress. Supply those resources using workload identity and provider-specific configuration.

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: dictionary-app
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: dictionary-app-orleans
rules:
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["get", "list", "watch", "patch"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: dictionary-app-orleans
subjects:
  - kind: ServiceAccount
    name: dictionary-app
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: Role
  name: dictionary-app-orleans
---
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
        orleans/serviceId: dictionary-app
        orleans/clusterId: production
    spec:
      serviceAccountName: dictionary-app
      automountServiceAccountToken: true
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
            - name: POD_NAMESPACE
              valueFrom:
                fieldRef:
                  fieldPath: metadata.namespace
            - name: POD_IP
              valueFrom:
                fieldRef:
                  fieldPath: status.podIP
            - name: ORLEANS_SERVICE_ID
              valueFrom:
                fieldRef:
                  fieldPath: metadata.labels['orleans/serviceId']
            - name: ORLEANS_CLUSTER_ID
              valueFrom:
                fieldRef:
                  fieldPath: metadata.labels['orleans/clusterId']
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

The role is namespace-scoped. It permits the hosting integration to read pods and ensure the Orleans identity labels on its own pod are correct. If you explicitly enable `KubernetesHostingOptions.DeleteDefunctSiloPods`, also grant the `delete` verb. Keep that option disabled unless the operational consequences have been reviewed.

## Network requirements

Allow direct pod-IP TCP traffic:

- Every silo pod to port `11111` on every silo pod.
- Every Orleans client to port `30000` on every silo pod.
- Every silo and client to the clustering provider.
- Silo pods to the Kubernetes API when `UseKubernetesHosting` is enabled.

Don't place a Kubernetes `Service` virtual IP in `AdvertisedIPAddress`. Orleans advertises each pod IP so peers can contact that specific silo. A `Service` can expose application HTTP ingress, but it doesn't replace Orleans membership or direct silo connectivity.

If a service mesh intercepts TCP, validate long-lived connections, pod-address preservation, mutual TLS policy, shutdown ordering, and retries. Exclude Orleans ports from interception if the mesh can't preserve the required semantics.

## Health probes

The three paths in the manifest are placeholders that the application must implement according to [Health and observability](health-and-observability.md):

- Startup succeeds after the silo joins the cluster and required initialization completes.
- Readiness becomes false before shutdown and whenever the application can't safely accept new traffic.
- Liveness performs only a local forward-progress check and doesn't call grains or dependencies.

The five-minute startup allowance is an example, not a universal default. Set it from measured startup and recovery time. Aggressive probes can turn provider latency or CPU pressure into a cluster-wide restart loop.

## Shutdown, rollouts, and scaling

Kubernetes sends `SIGTERM` and begins the pod termination grace period. The .NET host must observe the signal, report not ready, and receive enough time to stop Orleans gracefully. In the example, the 150-second pod grace period is longer than the 120-second host shutdown timeout.

Keep `maxUnavailable: 0` and some surge capacity when the cluster can't tolerate losing a ready silo during rollout. The PodDisruptionBudget protects against voluntary disruptions, not node failure.

Scale in gradually. Verify that membership stabilizes and remaining silos have capacity before removing more pods. See [Graceful shutdown and upgrades](upgrades.md).

## Troubleshoot the integration

If startup reports missing `KUBERNETES_SERVICE_HOST` or `KUBERNETES_SERVICE_PORT`, verify that the process is running in a pod and that service environment links haven't been disabled. If the API returns `403 Forbidden`, check the pod's service account, role binding namespace, and the required pod verbs.

Compare pod labels, `POD_NAME`, `POD_NAMESPACE`, `POD_IP`, service ID, cluster ID, and the advertised membership endpoint. For broader triage, see [Troubleshoot deployments](troubleshooting-deployments.md).
