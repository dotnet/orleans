# Docker - Kubernetes - ASP.NET Core

## Description

This sample creates two Docker containers, one that creates the Silo that hosts `ValueGrain` instances (you can find it in the `Grains/ValueGrain.cs` file) and a ASP.NET Core app (which acts like a client to this Silo) called `API` and resides in the `API` folder. User can access the API app and create/update `ValueGrain` instances. 
These two containers can be tested using [Docker Compose](https://docs.docker.com/compose/) or in a [Kubernetes](https://www.kubernetes.io) cluster. 

## Before running the sample

Before running the sample, you should

- Edit `API/startup.cs` and `Silo/Program.cs` and add your Azure Storage Connection string. However, bear in mind that in production environments the recommended way to set configuration information is via environment variables
- Edit `Makefile` and modify the `REGISTRY`, `LOCAL_K8S_CLUSTER` and `REMOTE_K8S_CLUSTER` with the appropriate values for your setup

## Docker compose

To build and run the sample using [Docker Compose](https://docs.docker.com/compose/) run:

```bash
docker-compose up
```

Then, on another command prompt, you can do `docker ps` to find out the port the the API service is listening.

```
CONTAINER ID        IMAGE               COMMAND             CREATED             STATUS              PORTS                   NAMES
9af86b6a00fa        api                 "dotnet API.dll"    31 seconds ago      Up 26 seconds       0.0.0.0:32771->80/tcp   docker-aspnet-core_api_1
3e71db40b80e        silo                "dotnet Silo.dll"   31 seconds ago      Up 27 seconds                               docker-aspnet-core_silo_1
```

As you can see, API is listening to port *32771* (your port will be different, of course). Now, you can use your browser and navigate to `http://localhost:32771` to test the app.

To stop the sample you can use `Ctrl-C`, whereas to delete resources you should run:

```bash
docker-compose down
```

## Kubernetes

We've added a `Makefile` as well as a `deploy.yaml` file with the necessary commands to run the solution on a Kubernetes cluster.

### Local Kubernetes cluster

You can run the sample on your local Kubernetes cluster. We have tested this with [Docker for Windows](https://docs.docker.com/docker-for-windows/) but it should also work with [Docker for Mac](https://docs.docker.com/docker-for-mac/) or [Minikube](https://kubernetes.io/docs/setup/minikube/) as well. To run it on local cluster, use a Linux shell or [WSL](https://docs.microsoft.com/en-us/windows/wsl/install-win10) on Windows and run:

```bash
make buildlocal
make deploylocal
```

Use your browser and navigate to `http://localhost:8888` to test the app.

If you want to remove resources, use:

```bash
make cleandeploylocal
```

### Remote Kubernetes cluster

To run the sample on your remote Kubernetes cluster, use:

```bash
make buildremote
make pushremote
make deployremote
# if you want to do it in one step, you can use make buildremote pushremote deployremote
```

You can use `kubectl get svc` to get the Public IP for the API [Kubernetes Service](https://kubernetes.io/docs/concepts/services-networking/service/), so you can connect via web browser. Alternatively, you can do `kubectl port-forward service/orleans-api 8888:8888` to create a tunnel between your machine and the remote Service. This way, you can access it via web browser at `http://localhost:8888`.

If you want to remove remote resources, use:

```bash
make cleandeployremote
```

### Using StatefulSets

Some advice (courtesy of [James Sukhabut](https://github.com/jsukhabut)) on using [StatefulSet](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/) to run the Silos.

StatefulSets are shutdown, scaled, or upgraded in order (ie.  4,3,2,1,0). When you try to upgrade your silo with a new deployment, if you use stateless deployment you have 5 brand new silos so it will take some time for them to get into a quorum. Using StatefulSets, first instance 4 gets shutdown and upgraded, then instance 3, then 2, 1, and 0. This technique can avoid downtime on upgrades. 

Regarding Orleans clients on Kubernetes, you can set the Silo as a Kubernetes Service with ClusterIP=None.  When this is done the silo DNS name (e.g. mysilo.mynamespace.svc.cluster.local) will have a multi-IP response containing all the IPs of the Silo Pods.  You can use a custom DnsNameGatewayListProvider class at the client that resolves the name to IP.  If the Silo scales up or down the client will easily see the updated list.  This can be much faster compared to the client reading Azure tables because the rows may contain Silos that are gone but not marked as dead yet. Kubernetes will always return the active silo (IP). You can find some code in  [this Pull Request](https://github.com/dotnet/orleans/pull/4301)

### Additional stuff

- If you have issues with Silos not being able to get activated when you re-deploy/update your Deployments, try deleting all rows from the Azure Table Storage membership table.
- We're using `imagePullPolicy: Always` so you would not need to increment the `VERSION` variable on the Makefile, but be aware that you may have to, just in case. Oh, and don't use `latest` in Production ([source](https://kubernetes.io/docs/concepts/configuration/overview/#container-images)).
- If you would like to use Kubernetes to store Membership information (via Custom Resource Definitions), check out [this](https://github.com/OrleansContrib/Orleans.Clustering.Kubernetes) project.
- We deactivated Server Garbage Collection on the API and Silo containers (check the corresponding .csproj files) because of [this](https://blog.markvincze.com/troubleshooting-high-memory-usage-with-asp-net-core-on-kubernetes/).
