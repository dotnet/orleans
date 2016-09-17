Orleans Service Fabric Samples
==

This solution contains samples demonstrating how to host an Orleans cluster on Service Fabric and connect to it using a client.

Service Fabric services can be classified into Stateless and Stateful services. Stateless services do not hold any reliable state whereas Stateful services have state which is replicated amongst a set of nodes with the help of Service Fabric.

StatelessCalculatorApp
==

This application contains a single, statelss service: StatelessCalculatorService.
The Orleans cluster is initialized in StatelessCalculatorService.CreateServiceInstanceListeners().
Two endpoints are added to PackageRoot\ServiceManifest.xml:
	  <Endpoint Name="OrleansSiloEndpoint" Protocol="tcp"/>
      <Endpoint Name="OrleansProxyEndpoint" Protocol="tcp"/>

With those endpoints and the ServiceInstanceListener present, all that's left to do is write some grain code, reference it in the StatelessCalculatorService project, and hit F5.

Note that the service shows the minimum integration to host an Orleans cluster on Service Fabric. The hosted cluster uses the Azure Table Storage membership provider.
