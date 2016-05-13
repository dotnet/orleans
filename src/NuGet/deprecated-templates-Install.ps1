param($installPath, $toolsPath, $package, $project)

write-host Removing this package since it is deprecated in favor of Microsoft.Orleans.OrleansCodeGenerator.Build

uninstall-package $package.Id -ProjectName $project.Name
