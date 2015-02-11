param($installPath, $toolsPath, $package, $project)

$configXml = $project.ProjectItems.Item("ClientConfiguration.xml")
$copyToOutput = $configXml.Properties.Item("CopyToOutputDirectory")
$copyToOutput.Value = 2