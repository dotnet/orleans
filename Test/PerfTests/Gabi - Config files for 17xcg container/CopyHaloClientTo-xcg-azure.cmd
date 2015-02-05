set machine=\\17xcg08%1

ECHO ""
ECHO ""
ECHO ------------ COPY CLIENT to %machine% ------------


del /q /s %machine%\c$\Users\gkliot\Documents\applications\HaloClient
del /q /s C:\Depot\Orleans\Code\Prototype\Test\HaloPresence\PresenceConsoleTest\bin\Debug\*.log


xcopy /s /y  C:\Depot\Orleans\Code\Prototype\Test\HaloPresence\PresenceConsoleTest\bin\Debug                    %machine%\c$\users\gkliot\documents\applications\HaloClient
xcopy /s /y  C:\Depot\Orleans\Code\Binaries\SDK-DROP\RemoteDeployment\ClientConfiguration.xml  			%machine%\c$\users\gkliot\documents\applications\HaloClient
xcopy /s /y  C:\Depot\Orleans\Code\Binaries\SDK-DROP\RemoteDeployment\StartHaloClient-%1.bat  	        %machine%\c$\users\gkliot\documents\applications\HaloClient

xcopy /s /y  C:\Depot\Orleans\Code\Prototype\Graphs\DistributedGraph-Orleans-V2\Cluster-Gabi-Scripts\OneMachine-Remote\Microsoft.VisualStudio.QualityTools.UnitTestFramework.dll  %machine%\c$\users\gkliot\documents\applications\HaloClient
xcopy /s /y  "C:\Program Files (x86)\Reference Assemblies\Microsoft\FSharp\2.0\Runtime\v4.0\FSharp.Core.dll"  %machine%\c$\users\gkliot\documents\applications\HaloClient
xcopy /s /y  "C:\Program Files\Reference Assemblies\Microsoft\FSharp\2.0\Runtime\v4.0\FSharp.Core.dll"        %machine%\c$\users\gkliot\documents\applications\HaloClient

