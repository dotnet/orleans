HelloWorld sample has two solutions: HelloWorld.SingleProcess and HelloWorld.SeparateProcess. 

- HelloWorld.SingleProcess.sln: 
  Orleans silo host and Orleans client are configured in the same project 'HelloWorld', and also running in the same process. To start this sample solution, set HelloWorld as the Startup project and start.

- HelloWorld.SeparateProcess.sln:
  Orleans client and Orleans silo host are configured in two different projects, and hence Orleans client and silo host are running in separate processes.
  To start this sample, you need to set both project OrleansClient and OrleansSiloHost as Startup project. 
  You can do this by going to the 'Properties' option for HelloWorld.SeparateProcess.sln, abd under 'Common Properties' -> 'Startup Project', choose 'Multiple startup projects'. Set OrleansClient and OrleansSiloHost to 'Start'
