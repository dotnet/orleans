set machine=\\17xcgXXX
set DST=%machine%\c$\users\XXX\documents\applications\LoadClient
set SRC=E:\Depot\Orleans\Code\Main\Test\HaloPresence\GrainBenchmarkLoadTest\

ECHO ""
ECHO ""
ECHO ------------ COPY CLIENT to %machine% to %DST% ------------

del /q /s %DST%


xcopy /y  %SRC%\bin\Debug\*																					%DST%
xcopy /y  %SRC%\References\Microsoft.VisualStudio.QualityTools.UnitTestFramework.dll 						%DST%

xcopy /y  "%SRC%\ClientConfiguration.xml"  				%DST%
xcopy /y  "%SRC%\References\START_Load_Client.bat"  	%DST%


pause