---
title: Kubernetes hosting
description: Learn how to host an Orleans app with Kubernetes.
ms.date: 05/23/2025
ms.topic: how-to
ms.custom:
  - devops
  - sfi-ropc-nochange
---

# Kubernetes hosting

Kubernetes is a popular choice for hosting Orleans applications. Orleans runs in Kubernetes without specific configuration; however, it can also take advantage of extra knowledge the hosting platform provides.

The [`Microsoft.Orleans.Hosting.Kubernetes`](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.Kubernetes) package adds integration for hosting an Orleans application in a Kubernetes cluster. The package provides an extension method, <xref:Orleans.Hosting.KubernetesHostingExtensions.UseKubernetesHosting*>, performing the following actions:

- Sets <xref:Orleans.Configuration.SiloOptions.SiloName?displayProperty=nameWithType> to the pod name.
- Sets <xref:Orleans.Configuration.EndpointOptions.AdvertisedIPAddress?displayProperty=nameWithType> to the pod IP.
- Configures <xref:Orleans.Configuration.EndpointOptions.SiloListeningEndpoint?displayProperty=nameWithType> & <xref:Orleans.Configuration.EndpointOptions.GatewayListeningEndpoint?displayProperty=nameWithType> to listen on any address, using the configured <xref:Orleans.Configuration.EndpointOptions.SiloPort> and <xref:Orleans.Configuration.EndpointOptions.GatewayPort>. Default port values of `11111` and `30000` are used if values aren't set explicitly.
- Sets <xref:Orleans.Configuration.ClusterOptions.ServiceId?displayProperty=nameWithType> to the value of the pod label named `orleans/serviceId`.
- Sets <xref:Orleans.Configuration.ClusterOptions.ClusterId?displayProperty=nameWithType> to the value of the pod label named `orleans/clusterId`.
- Early in the startup process, the silo probes Kubernetes to find which silos don't have corresponding pods and marks those silos as dead.
- The same process occurs at runtime for a subset of all silos to reduce the load on Kubernetes' API server. By default, 2 silos in the cluster watch Kubernetes.

Note that the Kubernetes hosting package doesn't use Kubernetes for clustering. A separate clustering provider is still needed. For more information on configuring clustering, see the [Server configuration](../host/configuration-guide/server-configuration.md) documentation.

## Deploy to Kubernetes with Aspire

Aspire simplifies Kubernetes deployment by automatically generating Kubernetes manifests from your AppHost configuration. Instead of manually writing YAML files, Aspire can produce the necessary deployment, service, and configuration resources based on your application's dependency graph.

### Add the Kubernetes hosting integration

First, install the [Aspire.Hosting.Kubernetes](https://www.nuget.org/packages/Aspire.Hosting.Kubernetes) NuGet package in your AppHost project:

```bash
dotnet add package Aspire.Hosting.Kubernetes
```

Then add a Kubernetes environment to your AppHost:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add the Kubernetes environment
var k8s = builder.AddKubernetesEnvironment("k8s");

// Add your Orleans silo project
var silo = builder.AddProject<Projects.MySilo>("silo");

builder.Build().Run();
```

You can optionally customize the Kubernetes environment properties:

```csharp
builder.AddKubernetesEnvironment("k8s")
    .WithProperties(k8s =>
    {
        k8s.HelmChartName = "my-orleans-app";
    });
```

### Generate Kubernetes manifests

Use the Aspire CLI to generate Kubernetes manifests from your Aspire project:

```bash
aspire publish -o ./k8s-manifests
```

This generates a complete set of Kubernetes YAML files in the specified output directory, including:

- **Deployments or StatefulSets** for each service in your AppHost
- **Services** for network connectivity
- **ConfigMaps and Secrets** for configuration
- **Helm charts** for easier deployment management
- **PersistentVolumes and PersistentVolumeClaims** for storage resources

### Deploy to your cluster

After generating the manifests, deploy them using `kubectl` or Helm:

```bash
# Using kubectl
kubectl apply -f ./k8s-manifests

# Or using Helm (if Helm charts were generated)
helm install my-orleans-app ./k8s-manifests/charts/my-orleans-app
```

### Benefits of Aspire-generated manifests

- **Consistent configuration**: Environment variables, ports, and resource references are automatically synchronized.
- **Dependency management**: Services are configured with correct connection strings and service references.
- **Orleans-aware**: The Orleans hosting integration ensures proper silo configuration is included.
- **Helm support**: Generated Helm charts allow parameterized deployments across environments.

> [!TIP]
> For production deployments, review and customize the generated manifests as needed. Use [external parameters](https://aspire.dev/get-started/resources/) to configure environment-specific values.

For detailed information about the Aspire Kubernetes integration, see the [Aspire Kubernetes integration documentation](https://aspire.dev/integrations/compute/kubernetes/).

For more information about configuring Orleans with Aspire, see [Aspire Orleans integration](../host/aspire-integration.md).

## Manual Kubernetes configuration

This functionality imposes some requirements on service deployment:

- Silo names must match pod names.
- Pods must have `orleans/serviceId` and `orleans/clusterId` labels corresponding to the silo's <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId>. The `UseKubernetesHosting` method propagates these labels into the corresponding Orleans options from environment variables.
- Pods must have the following environment variables set: `POD_NAME`, `POD_NAMESPACE`, `POD_IP`, `ORLEANS_SERVICE_ID`, `ORLEANS_CLUSTER_ID`.

The following example shows how to configure these labels and environment variables correctly:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: dictionary-app
  labels:
    orleans/serviceId: dictionary-app
spec:
  selector:
    matchLabels:
      orleans/serviceId: dictionary-app
  replicas: 3
  template:
    metadata:
      labels:
        # This label identifies the service to Orleans
        orleans/serviceId: dictionary-app

        # This label identifies an instance of a cluster to Orleans.
        # Typically, this is the same value as the previous label, or any
        # fixed value.
        # In cases where you don't use rolling deployments (for example,
        # blue/green deployments),
        # this value can allow for distinct clusters that don't communicate
        # directly with each other,
        # but still share the same storage and other resources.
        orleans/clusterId: dictionary-app
    spec:
      containers:
        - name: main
          image: my-registry.azurecr.io/my-image
          imagePullPolicy: Always
          ports:
          # Define the ports Orleans uses
          - containerPort: 11111
          - containerPort: 30000
          env:
          # The Azure Storage connection string for clustering is injected as an
          # environment variable.
          # You must create it separately using a command such as:
          # > kubectl create secret generic az-storage-acct `
          #     --from-file=key=./az-storage-acct.txt
          - name: STORAGE_CONNECTION_STRING
            valueFrom:
              secretKeyRef:
                name: az-storage-acct
                key: key
          # Configure settings to let Orleans know which cluster it belongs to
          # and which pod it's running in.
          - name: ORLEANS_SERVICE_ID
            valueFrom:
              fieldRef:
                fieldPath: metadata.labels['orleans/serviceId']
          - name: ORLEANS_CLUSTER_ID
            valueFrom:
              fieldRef:
                fieldPath: metadata.labels['orleans/clusterId']
          - name: POD_NAMESPACE
            valueFrom:
              fieldRef:
                fieldPath: metadata.namespace
          - name: POD_NAME
            valueFrom:
              fieldRef:
                fieldPath: metadata.name
          - name: POD_IP
            valueFrom:
              fieldRef:
                fieldPath: status.podIP
          - name: DOTNET_SHUTDOWNTIMEOUTSECONDS
            value: "120"
          request:
            # Set resource requests
      terminationGracePeriodSeconds: 180
      imagePullSecrets:
        - name: my-image-pull-secret
  minReadySeconds: 60
  strategy:
    rollingUpdate:
      maxUnavailable: 0
      maxSurge: 1
```

For RBAC-enabled clusters, granting the required access to the Kubernetes service account for the pods might also be necessary:

```yaml
kind: Role
apiVersion: rbac.authorization.k8s.io/v1
metadata:
  name: orleans-hosting
rules:
- apiGroups: [ "" ]
  resources: ["pods"]
  verbs: ["get", "watch", "list", "delete", "patch"]
---
kind: RoleBinding
apiVersion: rbac.authorization.k8s.io/v1
metadata:
  name: orleans-hosting-binding
subjects:
- kind: ServiceAccount
  name: default
  apiGroup: ''
roleRef:
  kind: Role
  name: orleans-hosting
  apiGroup: ''
```

## Liveness, readiness, and startup probes

Kubernetes can probe pods to determine service health. For more information, see [Configure Liveness, Readiness and Startup Probes](https://kubernetes.io/docs/tasks/configure-pod-container/configure-liveness-readiness-startup-probes/) in the Kubernetes documentation.

Orleans uses a cluster membership protocol to promptly detect and recover from process or network failures. Each node monitors a subset of other nodes, sending periodic probes. If a node fails to respond to multiple successive probes from multiple other nodes, the cluster forcibly removes it. Once a failed node learns of its removal, it terminates immediately. Kubernetes restarts the terminated process, which then attempts to rejoin the cluster.

Kubernetes probes help determine whether a process in a pod is executing and isn't stuck in a zombie state. These probes don't verify inter-pod connectivity or responsiveness, nor do they perform application-level functionality checks. If a pod fails to respond to a liveness probe, Kubernetes might eventually terminate that pod and reschedule it. Kubernetes probes and Orleans probes are therefore complementary.

The recommended approach is configuring Liveness Probes in Kubernetes that perform a simple, local-only check that the application performs as intended. These probes serve to terminate the process if there's a total freeze, for example, due to a runtime fault or another unlikely event.

## Resource quotas

Kubernetes works with the operating system to implement [resource quotas](https://kubernetes.io/docs/concepts/policy/resource-quotas/). This allows enforcing CPU and memory reservations and/or limits. For a primary application serving interactive load, implementing restrictive limits isn't recommended unless necessary. It's important to note that requests and limits differ substantially in meaning and implementation location. Before setting requests or limits, take time to gain a detailed understanding of how they are implemented and enforced. For example, memory might not be measured uniformly between Kubernetes, the Linux kernel, and the monitoring system. CPU quotas might not be enforced as expected.

## Troubleshooting

### Pods crash, complaining that `KUBERNETES_SERVICE_HOST and KUBERNETES_SERVICE_PORT must be defined`

Full exception message:

```Output
Unhandled exception. k8s.Exceptions.KubeConfigException: unable to load in-cluster configuration, KUBERNETES_SERVICE_HOST and KUBERNETES_SERVICE_PORT must be defined
at k8s.KubernetesClientConfiguration.InClusterConfig()
```

- Check that the `KUBERNETES_SERVICE_HOST` and `KUBERNETES_SERVICE_PORT` environment variables are set inside the Pod. Check by executing the command `kubectl exec -it <pod_name> /bin/bash -c env`.
- Ensure `automountServiceAccountToken` is set to `true` in the Kubernetes `deployment.yaml`. For more information, see [Configure Service Accounts for Pods](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/).
