param($installPath, $toolsPath, $package, $project)

$bondSerializerTypeName = 'Orleans.Serialization.BondSerializer, OrleansBondUtils'

function UnregisterSerializer(
    [OutputType([void])]
    [Parameter(Mandatory=$true)]
    [string]$fileKey,
    [Parameter(Mandatory=$true)]
    [System.__ComObject]$project,
    [Parameter(Mandatory=$true)]
    [string]$type
    )
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
        Write-Host "Removing Bond Serializer from $fileKey"
    }
    Catch
    {
        return
    }
    
    if ($projectItem -eq $null) {
        Write-Error "The project item for $fileKey was null"
        return
    }

	$projectItem.Open()
    
	$documentEntry = $projectItem.Document
    if ($documentEntry -eq $null) {
        Write-Error "The fileKey $fileKey had no associated document"
        return
    }

	$documentEntry.Activate() | Out-Null
    $filePath = $documentEntry.FullName
    if ([System.IO.File]::Exists($filePath) -eq $false) {
        Write-Error "The file $filePath was not found"
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
        $providerNode.ParentNode.RemoveChild($providerNode) | Out-Null
        $fileXml.Save($filePath)
    }

	$documentEntry.Close()
}

UnregisterSerializer -fileKey "OrleansConfiguration.xml" -project $project -type $bondSerializerTypeName
UnregisterSerializer -fileKey "ClientConfiguration.xml" -project $project -type $bondSerializerTypeName