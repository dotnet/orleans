param($installPath, $toolsPath, $package, $project)

$bondSerializerTypeName = 'Orleans.Serialization.BondSerializer, BondSerializer'

function AddOrGetElement(
    [OutputType([System.Xml.XmlElement])]
    [Parameter(Mandatory=$true)]
    [System.Xml.XmlElement]$xml,
    [Parameter(Mandatory=$true)]
    [string]$name,
    [Parameter(Mandatory=$true)]
    [System.Xml.XmlNamespaceManager]$namespaceManager    
    )
{
    $node = $xml.ChildNodes | where { $_.Name -eq $name }
    if ($node -eq $null)
    {
        $node = $xml.OwnerDocument.CreateElement($name, "urn:orleans");
        $xml.AppendChild($node) | Out-Null
    }

    return $node
}

function RegisterSerializer(
    [Parameter(Mandatory=$true)]
    [string]$filePath,
    [Parameter(Mandatory=$true)]
    [string]$type)
{
	$fileXml = [xml](Get-Content $filePath)
	if ($fileXml -eq $null) {Q
		return
	}

    $nameTable = $fileXml.NameTable
    $namespaceManager = New-Object -TypeName System.Xml.XmlNamespaceManager -ArgumentList $nameTable
    $namespaceManager.AddNamespace([string]::Empty, "urn:orleans");
    $namespaceManager.AddNamespace("o", "urn:orleans");
    $rootNode = $fileXml.DocumentElement;
	$isServerConfig = $fileXml.SelectSingleNode("/o:OrleansConfiguration", $namespaceManager) -ne $null
    if ($isServerConfig) {
        $globalsNode = AddOrGetElement -xml $rootNode -name "Globals" -namespaceManager $namespaceManager
        $parentNode = $globalsNode
    } else {
        $parentNode = $rootNode
    }

    $messagingNode = AddOrGetElement -xml $parentNode -name "Messaging" -namespaceManager $namespaceManager
    $providersNode = AddOrGetElement -xml $messagingNode -name "SerializationProviders" -namespaceManager $namespaceManager

    $bondTypeProvider = $providersnode.Provider | where { $_.type -eq $type }

    if ($bondTypeProvider -eq $null) 
    {
        $provider = AddOrGetElement -xml $providersNode -name "Provider" -namespaceManager $namespaceManager
        $typeAttribute = $fileXml.CreateAttribute("type");
        $typeAttribute.Value = $type
        $provider.Attributes.Append($typeAttribute) | Out-Null
        $fileXml.Save($filePath);
    }

	Out-Null
}

$configXml = $project.ProjectItems.Item("OrleansConfiguration.xml")
if ($configXml -ne $null -and $configXml.Document -ne $null) {
	RegisterSerializer -filePath $configXml.Document.FullName -type $bondSerializerTypeName
}

$configXml = $project.ProjectItems.Item("ClientConfiguration.xml")
if ($configXml -ne $null -and $configXml.Document -ne $null) {
	RegisterSerializer -filePath $configXml.Document.FullName -type $bondSerializerTypeName
}
