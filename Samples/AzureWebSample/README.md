# Azure Web Sample #

An Azure-hosted version of Hello World.

Runs an Orleans silo in an Azure worker role, and an Azure web role acting as a client talking to the HelloWorld grain in the silo.
One important thing that should not be missed is how ServerGC is configured for the Silo worker role in the ServiceDefinition.csdef

The sample is configured to run inside of the Azure Compute Emulator on your desktop by default.

More info about this sample is available here:
http://dotnet.github.io/orleans/Documentation/Samples-Overview/Azure-Web-Sample.html
