$OrleansGameClient=New-Object System.Collections.ArrayList
$OrleansDirectoryService=New-Object System.Collections.ArrayList
$OrleansDirectoryServiceConfigFile=New-Object System.Collections.ArrayList
$OrleansHost =New-Object System.Collections.ArrayList
$CopyConfigFileTo =New-Object System.Collections.ArrayList
$CopyConfigFileFrom =New-Object System.Collections.ArrayList
Function ComponentCOnfig{
param($ConfigfilePath)


$Config = gc $ConfigfilePath 

foreach( $line in $Config)
{

    $b =$line.Split("|")
   
    if( $b[0].trim() -eq "OrleansDirectoryService")
    {
    $OrleansDirectoryService.add($b[1].trim())
    }
    
     if( $b[0].trim() -eq "OrleansDirectoryServiceConfigFile")
    {
    $OrleansDirectoryServiceConfigFile.add($b[1].trim())
    }
    
    if( $b[0].trim() -eq "OrleansHost")
    {
    $OrleansHost.add($b[1].trim())
    }
    
    if( $b[0].trim() -eq "OrleansGameClient")
    {
    $OrleansGameClient.add($b[1].trim())
    }
    
    if( $b[0].trim() -eq "CopyConfigFileTo")
    {
    $CopyConfigFileTo.add($b[1].trim())
    }
    
    if( $b[0].trim() -eq "CopyConfigFileFrom")
    {
    $CopyConfigFileFrom.add($b[1].trim())
    }
    
 
}

#Write-Host "ds" $OrleansDirectoryService
#Write-Host "oh" $OrleansHost
#Write-Host "gc" $OrleansGameClient

}

# ComponentConfig C:\Scripts\OrleansdeploymentConfig.txt
#$OrleansDirectoryService