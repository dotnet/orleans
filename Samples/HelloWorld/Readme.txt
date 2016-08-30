HelloWorld sample has two solution: HelloWorld.SingleProcess and HelloWorld.SeparateProcess. 

HelloWorld.SingleProcess sln: 
Orleans silo host and Orleans client is configured in the same project 'HelloWorld', and also running in the same process. To start 
this sample solution, set HelloWorld as StartUp project and start.

HelloWorld.SeparateProcess sln:
Orleans client and Orleans silo host are configured in two different projects, and also Orleans client and silo host is running in 
separate processes.
To start this sample, you need to set both project OrleansClient and OrleansSiloHost as StartUp project. 
You can do that by going to 'Properties' tab for HelloWorld.SeparateProcess sln, under 'Common Properties' -> 'StartUp Project', choose
'Multiple start up projects' and set OrleansClient and OrleansSiloHost as 'Start'