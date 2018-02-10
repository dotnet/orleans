---
layout: page
title: Container networking
---

It is important that silo and client can communicate between each other. The easiest way to ensure that is that they both are on the same network. 

When a silo starts, it will write its IP Adress on the Membership Table. This entry will then be used by other silos and clients from the cluster to interact with it.

If silos are "multi-homed" (for example, clients and silos are both on the "orleans-net", but silos are also on the "backend-net"), it is possible that orleans will publish the wrong network address in the membership table. To prevent this, you should configure the correct subnet to use in the Orleans configuration
(`GlobalConfiguration.Subnet`)

# Creating a network if you are using Docker Swarm

You can create a network overlay (named "orleans-net') by specifying the subnet like this:

```
docker network create \
  --driver overlay \
  --subnet 10.0.9.0/24 \
  --gateway 10.0.9.99 \
  orleans-net
```

# Creating a network if you are using Kubernetes

Please refer to the documentation of the networking pod that you are using.