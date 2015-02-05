del /q /s \\Xcg-azure-30\c$\Users\gkliot\Documents\applications\Test
del /q /s C:\Depot\Orleans\Code\Prototype\OrleansV4\Test\bin\Debug\*.log

ECHO ------------ COPY CLIENT ------------

xcopy /y C:\Depot\Orleans\Code\Prototype\OrleansV4\Test\bin\Debug\UnitTestGrains.*                 C:\Depot\Orleans\Code\Prototype\OrleansV4\Test\bin\Debug\Applications\UnitTests\
xcopy /y C:\Depot\Orleans\Code\Prototype\OrleansV4\Test\bin\Debug\UnitTestGrainInterfaces.*        C:\Depot\Orleans\Code\Prototype\OrleansV4\Test\bin\Debug\Applications\UnitTests\

xcopy /s /y  C:\Depot\Orleans\Code\Prototype\OrleansV4\Test\bin\Debug                                           \\17xcg0830\c$\users\gkliot\documents\applications\Test
xcopy /s /y  C:\Depot\Orleans\Code\Binaries\SDK-DROP\RemoteDeployment\ClientConfiguration.xml  			\\17xcg0830\c$\users\gkliot\documents\applications\Test
xcopy /s /y  C:\Depot\Orleans\Code\Binaries\SDK-DROP\RemoteDeployment\StartTestClient.bat  				\\17xcg0830\c$\users\gkliot\documents\applications\Test

xcopy /s /y  C:\Depot\Orleans\Code\Prototype\Graphs\DistributedGraph-Orleans-V2\Cluster-Gabi-Scripts\OneMachine-Remote\Microsoft.VisualStudio.QualityTools.UnitTestFramework.dll  \\17xcg0830\c$\users\gkliot\documents\applications\Test
xcopy /s /y  "C:\Program Files (x86)\Reference Assemblies\Microsoft\FSharp\2.0\Runtime\v4.0\FSharp.Core.dll"  \\17xcg0830\c$\users\gkliot\documents\applications\Test
xcopy /s /y  "C:\Program Files\Reference Assemblies\Microsoft\FSharp\2.0\Runtime\v4.0\FSharp.Core.dll"        \\17xcg0830\c$\users\gkliot\documents\applications\Test



pause
