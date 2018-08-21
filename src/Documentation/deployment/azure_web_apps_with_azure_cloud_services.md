---
layout: page
title: Using Azure Web Apps with Azure Cloud Services
---

# Using Azure Web Apps with Azure Cloud Services

If you would like to connect to an Azure Cloud Services Silo from an [Azure Web App](http://azure.microsoft.com/en-gb/services/app-service/web/) rather than a Web Role hosted within the same cloud service you can.

For this to work securely you will need to assign both the Azure Web App and the Worker Role hosting the Silo to an [Azure Virtual Network](http://azure.microsoft.com/en-gb/services/virtual-network/).

First we'll setup the Azure Web App, you can follow [this guide](https://azure.microsoft.com/en-us/blog/azure-websites-virtual-network-integration/) which will create the virtual network and assign it to the Azure Web App.

Now we can assign the cloud service to the virtual network by modifying the `ServiceConfiguration` file.

``` xml
<NetworkConfiguration>
  <VirtualNetworkSite name="virtual-network-name" />
  <AddressAssignments>
    <InstanceAddress roleName="role-name">
      <Subnets>
        <Subnet name="subnet-name" />
      </Subnets>
    </InstanceAddress>
  </AddressAssignments>
</NetworkConfiguration>
```

Also make sure the Silo endpoints are configured.

``` xml
<Endpoints>
  <InternalEndpoint name="OrleansSiloEndpoint" protocol="tcp" port="11111" />
  <InternalEndpoint name="OrleansProxyEndpoint" protocol="tcp" port="30000" />
</Endpoints>
```

You can now connect from the Web App to the rest of the cluster.

### Potential Issues

If the Web App is having difficulty connecting to the Silo:

* Make sure you have at least **two roles**, or two instances of one role in your Azure Cloud Service, or the `InternalEndpoint` firewall rules may not be generated.
* Check that both the Web App and the Silo are using the same `ClusterId` and `ServiceId`.
* Make sure the network security group is set up to allow internal virtual network connections. If you haven't got one you can create and assign one easily using the following `PowerShell`:

``` c
New-AzureNetworkSecurityGroup -Name "Default" -Location "North Europe"
Get-AzureNetworkSecurityGroup -Name "Default" | Set-AzureNetworkSecurityGroupToSubnet -VirtualNetworkName "virtual-network-name" -SubnetName "subnet-name"
```
