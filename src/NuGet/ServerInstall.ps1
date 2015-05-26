param($installPath, $toolsPath, $package, $project)

$configXml = $project.ProjectItems.Item("OrleansConfiguration.xml")
$copyToOutput = $configXml.Properties.Item("CopyToOutputDirectory")
$copyToOutput.Value = 2