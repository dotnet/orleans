param($installPath, $toolsPath, $package, $project)

$bondSerializerTypeName = 'Orleans.Serialization.BondSerializer, OrleansBondUtils'

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
    [OutputType([void])]
    [Parameter(Mandatory=$true)]
    [string]$fileKey,
    [Parameter(Mandatory=$true)]
    [System.__ComObject]$project,
    [Parameter(Mandatory=$true)]
    [string]$type)
{
    Try
    {
    $project.Save([string]::Empty)
    }
    Catch 
    {
    }

    Try
    {
        $projectItem = $project.ProjectItems.Item($fileKey)
        Write-Host "Found $fileKey in project"
    }
    Catch
    {
        return
    }

    if ($projectItem -eq $null) {
        Write-Error "The fileKey $fileKey had a null entry"
        return
    }
    
    Try
    {
    $projectItem.Save([string]::Empty)
    }
    Catch
    {
    }
    
    $documentEntry = $projectItem.Document
    if ($documentEntry -eq $null) {
        Write-Error "The fileKey $fileKey had no associated document"
        return
    }
    
    $fullFilePath = $documentEntry.FullName
    
    if (-not [System.IO.File]::Exists($fullFilePath)) {
        Write-Error "The file $fullFilePath was not found"
        return
    }

    $fileXml = [xml](Get-Content $fullFilePath)
    if ($fileXml -eq $null) {
        Write-Error "Unable to load file xml $filePath"
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
        Write-Host Adding provider to provider list
        $provider = AddOrGetElement -xml $providersNode -name "Provider" -namespaceManager $namespaceManager
        $typeAttribute = $fileXml.CreateAttribute("type");
        $typeAttribute.Value = $type
        $provider.Attributes.Append($typeAttribute) | Out-Null
        Write-Host Saving updated $fileKey
        $fileXml.Save($fullFilePath)
    }

    Out-Null
}

RegisterSerializer -fileKey "OrleansConfiguration.xml" -project $project -type $bondSerializerTypeName
RegisterSerializer -fileKey "ClientConfiguration.xml" -project $project -type $bondSerializerTypeName
