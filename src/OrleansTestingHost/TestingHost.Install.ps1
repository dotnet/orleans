param($installPath, $toolsPath, $package, $project)

$configXml = $project.ProjectItems.Item("ClientConfigurationForTesting.xml")
$copyToOutput = $configXml.Properties.Item("CopyToOutputDirectory")
$copyToOutput.Value = 2

$configXml = $project.ProjectItems.Item("OrleansConfigurationForTesting.xml")
$copyToOutput = $configXml.Properties.Item("CopyToOutputDirectory")
$copyToOutput.Value = 2
