# function to find IP Address of Orleans Directory Service
function FindIP
{
param($server)
   $add = ping -n 1 -4 $server |% {$_.split(" ")}
  
	$add =  ((($add[4]).Replace('[',' ')).Replace(']',' ')).Trim()
	#$add = $add.Replace('[',' ')
	#$add= $add.Replace(']',' ')
	#$add =$add.Trim()
    
	return $add
}

# function to Change IP Address of COnfig File 
Function ChangeDSIP
{param($configfile, $ip)
   $ip
	$xml =New-Object XML
	$xml.Load($configfile)
	if  ($xml.configuration.NameServiceSection.HasAttributes -eq "True")
	{
	$xml.configuration.NameServiceSection.Address = $ip.tostring()
	}
	if  ($xml.configuration.DistributedMonitorSection.HasAttributes -eq "True")
	{
	$xml.configuration.DistributedMonitorSection.Address = $ip.tostring()
	}
	write-host $xml.configuration.NameServiceSection.Address
	$xml.Save($configfile)
}

#Main()

$buildPath = $args[0]

#copy latestbuild 
robocopy $buildPath .\OrleansDS /s
Write-Host "one"
robocopy $buildPath .\OrleansHost /s
robocopy $buildPath .\Gameclient /s

#parse the OrleansDeploymentConfig file and collect the information for deployment
. .\ComponentConfig.ps1
ComponentConfig C:\Scripts\OrleansDeploymentConfig.txt

Write-Host "ds" $OrleansDirectoryService
Write-Host "oh" $OrleansHost
Write-Host "gc" $OrleansGameClient
Write-Host "copyfrom" $CopyConfigFileFrom
Write-Host "configfile" $OrleansDirectoryServiceConfigFile
Write-Host "test1" $CopyConfigFileFrom + "\"  + $OrleansDirectoryServiceConfigFile ".\"

#copy the orleansdirectoryservice config file to the temp folder to change the ip address
copy $CopyConfigFileFrom"\"$OrleansDirectoryServiceConfigFile ".\"

#find the ipaddress of the orleans directory service that is found in the directory service config file.
. FindIP $OrleansDirectoryService

$add

#change the ip address in the orleans directory service config file
ChangeDSIP .\$OrleansDirectoryServiceConfigFile $add
$shell = New-Object -comObject WScript.Shell 
#copy the changed orleans directory service config file to other folders for deployment.
foreach ($dir in $CopyConfigFileTo)
{
   
    copy-item $OrleansDirectoryServiceConfigFile[0].tostring() $dir.tostring()
    
}

#Copying the required executable to the servers
foreach ($server in $OrleansDirectoryService)
{
    remove-item \\$server\c$\Orleans\OrleansDS  -force -recurse
    remove-item \\$server\c$\Orleans\NodeManager  -force -recurse
    robocopy .\OrleansDS \\$server\c$\Orleans\OrleansDS
    #robocopy .\NodeManager \\$server\c$\Orleans\NodeManager
	#start cmd winrs -r:$server -d:c:\Orleans\OrleansDS StartDirectoryService.cmd
    #$shell = New-Object -comObject WScript.Shell 
    #$shell.Run("winrs -r:$server -d:c:\Orleans\OrleansDS StartDirectoryService.cmd") 
    $shell.Run("c:\scripts\StartProcessWRS.cmd $server c:\Orleans\OrleansDS StartDirectoryService.cmd")
    
}

#Copying the required executable to the servers
foreach ($server in $OrleansHost)
{
    remove-item \\$server\c$\OrleansHost  -force -recurse
    robocopy .\OrleansHost \\$server\c$\OrleansHost
	$shell.Run("c:\scripts\StartProcessWRS $server c:\OrleansHost StartOrleans.cmd")
	#winrs -r:$server -d:c:\OrleansHost StartOrleans.cmd
}


#Copying the required executable to the servers
foreach ($server in $OrleansGameClient)
{
    remove-item \\$server\c$\GameClient  -force -recurse
    robocopy .\GameClient \\$server\c$\GameClient
	#winrs -r:$server -d:c:\GameClient Techfest.TetrisGameClient.exe
	$shell.Run("c:\scripts\StartProcessWRS $server c:\GameClient TetrisGameClientConsoleApp.exe GameSession=1")
}
