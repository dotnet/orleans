@echo Begin Post-Build script

copy /y "%SolutionDir%SDK\OrleansConfiguration.xml" "%TargetDir%"
copy /y "%SolutionDir%SDK\ClientConfiguration.xml" "%TargetDir%"

@echo BuildingInsideVisualStudio = %BuildingInsideVisualStudio% BuildOrleansNuGet = %BuildOrleansNuGet%

@echo Clean old generated Orleans NuGet packages from %TargetDir%
del /q *.nupkg

copy /y "%SolutionDir%NuGet\*.props" "%TargetDir%\"
copy /y "%SolutionDir%NuGet\EmptyFile.cs" "%TargetDir%\"
copy /y "%SolutionDir%NuGet\*Install.ps1" "%TargetDir%\"

if not "%BuildingInsideVisualStudio%"=="true" (
	@echo Build Orleans NuGet packages from %TargetDir%
	call "%SolutionDir%NuGet\CreateOrleansPackages.bat" . .\Version.txt
	if ERRORLEVEL 1 EXIT /B 1
)
