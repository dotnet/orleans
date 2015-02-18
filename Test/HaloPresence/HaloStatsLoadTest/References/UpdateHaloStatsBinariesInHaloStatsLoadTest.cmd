REM SRC is the root folder for your local copy of the Halo tree.
REM DST is the folder containing HaloPresence load test.
REM DSTSILO is the folder with LocalSilo.

set SRC=E:\Section-3\Services\Main\Products\Stats
set DST=E:\Depot\Orleans\Code\Main\Test\HaloPresence\HaloStats
set DSTSILO=E:\Depot\Orleans\Code\Binaries\SDK-DROP\LocalSilo\Applications\HaloStats


ECHO ""
ECHO ""
ECHO ------------ COPING HALO STATS DLL FROM %SRCGRAINS% to %DST% ------------

del /q /s %DST%

xcopy /y  %SRC%\Stats.Grains\bin\Debug										%DST%
xcopy /y  %SRC%\Tests\StatsTestCommon\bin\Debug\StatsTestCommon.* 			%DST%
xcopy /y  %SRC%\Tests\StatsTestCommon\bin\Debug\TestHelper.* 				%DST%

del /q /s %DST%\Orleans.*
del /q /s %DST%\Microsoft.WindowsAzure.*.*
del /q /s %DST%\OrleansRuntimeGrainInterfaces.*


ECHO ------------ NOW ALSO COPY GRAIN DLLS INTO LOCAL SILO %DSTSILO% ------------

del /q /s %DSTSILO%
xcopy /y  %DST% 				%DSTSILO%


pause