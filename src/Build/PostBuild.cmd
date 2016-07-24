@echo Begin Post-Build script
@setlocal EnableExtensions EnableDelayedExpansion

if "%BuildingInsideVisualStudio%" == "true" (
    set PKG_DIR=%SolutionDir%NuGet.Packages
) else (
    set PKG_DIR=%TargetDir%..\NuGet.Packages
)

if "%BuildingInsideVisualStudio%" == "true" (
    set CHOCO_PKG_DIR=%SolutionDir%Chocolatey.Packages
) else (
    set CHOCO_PKG_DIR=%TargetDir%..\Chocolatey.Packages
)

copy /y "%SolutionDir%SDK\OrleansConfiguration.xml" "%TargetDir%"
copy /y "%SolutionDir%SDK\ClientConfiguration.xml" "%TargetDir%"

if "%BuildOrleansNuGet%" == "" (
    set BuildOrleansNuGet=true
    if "%Configuration%" == "Release" (
        set BuildOrleansNuGetPrerelease=false
    ) else (
        set BuildOrleansNuGetPrerelease=true
    )
)

if "%BuildOrleansChocolatey%" == "" (
	if "%APPVEYOR%" == "True" (
        @echo == Cannot use Appveyor's Chocolatey packager v0.9.8, setting BuildOrleansChocolatey=false
		set BuildOrleansChocolatey=false
    ) else if "%Configuration%" == "Release" (
		where choco
		if ERRORLEVEL 1 (
            @echo == Chocolatey packager not found, setting BuildOrleansChocolatey=false
			set BuildOrleansChocolatey=false
		) else (
			choco
			set BuildOrleansChocolatey=true
		)
    ) else (
        set BuildOrleansChocolatey=false
    )
)

@echo BuildingInsideVisualStudio = %BuildingInsideVisualStudio% BuildOrleansNuGet = %BuildOrleansNuGet% BuildOrleansNuGetPrerelease = %BuildOrleansNuGetPrerelease% BuildOrleansChocolatey = %BuildOrleansChocolatey%

copy /y "%SolutionDir%NuGet\*.props" "%TargetDir%"
copy /y "%SolutionDir%NuGet\EmptyFile.cs" "%TargetDir%"
copy /y "%SolutionDir%NuGet\*Install.ps1" "%TargetDir%"
copy /y "%SolutionDir%NuGet\*Readme.txt" "%TargetDir%"

if not "%BuildingInsideVisualStudio%" == "true" (
    if "%BuildOrleansNuGet%" == "true" (
        @echo == Clean old generated Orleans NuGet packages from %TargetDir%
        del /q *.nupkg

        @echo ===== Build Orleans NuGet packages from %TargetDir%
        call "%SolutionDir%NuGet\CreateOrleansPackages.bat" . .\Version.txt %SolutionDir% %BuildOrleansNuGetPrerelease%
        if ERRORLEVEL 1 EXIT /B 1
    
        if "%BuildOrleansNuGetPrerelease%" == "true" (
            set PackageDir=%PKG_DIR%\Prerelease
        ) else (
            set PackageDir=%PKG_DIR%
        )
        
        @echo == Copying Orleans NuGet packages to !PackageDir!
        if not exist "!PackageDir!" (md "!PackageDir!") else (del /q "!PackageDir!\*")
        xcopy /y *.nupkg "!PackageDir!\"
        
        @echo == Clean transient generated Orleans NuGet packages from %TargetDir%
        del /q *.nupkg
    ) else (
        @echo ===== Skipping generation of Orleans NuGet packages for %Configuration% because BuildOrleansNuGet=%BuildOrleansNuGet%
    )

    if "%BuildOrleansChocolatey%" == "true" (
        @echo == Clean old generated Orleans Chocolatey / NuGet packages from %TargetDir%
        del /q *.nupkg

        @echo ===== Build Orleans Chocolatey packages from %TargetDir%
        call "%SolutionDir%Chocolatey\CreateOrleansChocolateyPackage.bat" . .\Version.txt
        if ERRORLEVEL 1 EXIT /B 2
    
        @echo == Copying Orleans Chocolatey packages to %CHOCO_PKG_DIR%
        if not exist "%CHOCO_PKG_DIR%" (md "%CHOCO_PKG_DIR%") else (del /s/q "%CHOCO_PKG_DIR%\*")
        xcopy /y *.nupkg "%CHOCO_PKG_DIR%\"
        @echo == Clean transient generated Orleans Chocolatey packages from %TargetDir%
        del /q *.nupkg
    ) else (
        @echo ===== Skipping generation of Orleans Chocolatey packages for %Configuration% because BuildOrleansChocolatey=%BuildOrleansChocolatey%
    )
)
