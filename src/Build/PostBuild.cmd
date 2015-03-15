@echo Begin Post-Build script

if "%BuildingInsideVisualStudio%" == "true" (
    set PKG_DIR=%SolutionDir%\NuGet.Packages
) else (
    set PKG_DIR=%TargetDir%\..\NuGet.Packages
)

copy /y "%SolutionDir%SDK\OrleansConfiguration.xml" "%TargetDir%"
copy /y "%SolutionDir%SDK\ClientConfiguration.xml" "%TargetDir%"

if "%BuildOrleansNuGet%" == "" (
    if "%Configuration%" == "Release" (
        set BuildOrleansNuGet=true
    ) else (
        set BuildOrleansNuGet=false
    )
)

@echo BuildingInsideVisualStudio = %BuildingInsideVisualStudio% BuildOrleansNuGet = %BuildOrleansNuGet%

copy /y "%SolutionDir%NuGet\*.props" "%TargetDir%\"
copy /y "%SolutionDir%NuGet\EmptyFile.cs" "%TargetDir%\"
copy /y "%SolutionDir%NuGet\*Install.ps1" "%TargetDir%\"

if not "%BuildingInsideVisualStudio%" == "true" (
    if "%BuildOrleansNuGet%" == "true" (
        @echo Clean old generated Orleans NuGet packages from %TargetDir%
        del /q *.nupkg

        @echo Build Orleans NuGet packages from %TargetDir%
        call "%SolutionDir%NuGet\CreateOrleansPackages.bat" . .\Version.txt
        if ERRORLEVEL 1 EXIT /B 1
    
        @echo Copying Orleans NuGet packages to %PKG_DIR%
        if not exist "%PKG_DIR%" (md "%PKG_DIR%") else (del /s/q "%PKG_DIR%\*")
        xcopy /y *.nupkg "%PKG_DIR%\"
    ) else (
        @echo Skipping generation of Orleans NuGet packages for Configuration=%Configuration% because BuildOrleansNuGet=%BuildOrleansNuGet%
    )
)
