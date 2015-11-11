param($installPath, $toolsPath, $package, $project)

$bondSerializerTypeName = 'Orleans.Serialization.BondSerializer, BondSerializer'

function UnregisterSerializer(
    [Parameter(Mandatory=$true)]
    [string]$filePath,
    [Parameter(Mandatory=$true)]
    [string]$type
    )
{
    if ([System.IO.File]::Exists($filePath) -eq $false) {
        return
    }

    $fileXml = [xml](Get-Content $filePath)
    if ($fileXml -eq $null)
    {
        return
    }

    $nameTable = $fileXml.NameTable
    $namespaceManager = New-Object -TypeName System.Xml.XmlNamespaceManager -ArgumentList $nameTable
    $namespaceManager.AddNamespace([string]::Empty, "urn:orleans");
    $namespaceManager.AddNamespace("o", "urn:orleans");
    $rootNode = $fileXml.DocumentElement;
    $providerNode = $rootNode.SelectSingleNode("//o:Provider[@type='$type']", $namespaceManager);

    if ($providerNode -ne $null)
    {
        $providerNode.ParentNode.RemoveChild($providerNode);
        $fileXml.Save($filePath);
    }

    Out-Null
}


$configXml = $project.ProjectItems.Item("OrleansConfiguration.xml")
if ($configXml -ne $null -and $configXml.Document -ne $null) {
	UnregisterSerializer -filePath $configXml.Document.FullName -type $bondSerializerTypeName
}

$configXml = $project.ProjectItems.Item("ClientConfiguration.xml")
if ($configXml -ne $null -and $configXml.Document -ne $null) {
	UnregisterSerializer -filePath $configXml.Document.FullName -type $bondSerializerTypeName
}
