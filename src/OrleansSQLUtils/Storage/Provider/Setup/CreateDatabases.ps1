$subscripitonName = "<sub name>"
$serverName = "<sql server name>"
$serverInstance = $serverName + ".database.windows.net"
$userName = "<sql user name>"
$userNameFull = $userName + "@" + $serverName
$password = "<sql password>"
$mgmtdb = "<sql elastic scale mgmt db name>"
$sharddbStem = "<sql elastic scale shard db name stem>"
$shardsCount = 8
$dbTier = "S0"
$dbEdition = "Standard"

$TKey = $([int])

$shardMapNameStem = "ShardMap"
$ranges = 1, 2, 4, 8

$ScriptDir = Split-Path -parent $MyInvocation.MyCommand.Path
Import-Module $ScriptDir\SqlDatabaseHelpers -Force
Import-Module $ScriptDir\ShardManagement -Force

Select-AzureSubscription -SubscriptionName $subscripitonName
$ctx = New-AzureSqlDatabaseServerContext -ServerName $serverName -UseSubscription
$so = Get-AzureSqlDatabaseServiceObjective $ctx -ServiceObjectiveName $dbTier

$db = Get-AzureSqlDatabase -ConnectionContext $ctx -DatabaseName $mgmtdb -ErrorAction Ignore
if (!$db) {
    Write-Output "Creating $mgmtdb" 
    $db = New-AzureSqlDatabase -ConnectionContext $ctx -DatabaseName $mgmtdb -Edition $dbEdition -ServiceObjective $so
} else {
    Write-Output "$mgmtdb already exists"
}

for ($nshard = 1; $nshard -le $shardsCount; $nshard++) {

    $sharddbName = $sharddbStem + $nshard
    $db = Get-AzureSqlDatabase -ConnectionContext $ctx -DatabaseName $sharddbName -ErrorAction Ignore
    if (!$db) {
        Write-Output "Creating $sharddbName" 
        $db = New-AzureSqlDatabase -ConnectionContext $ctx -DatabaseName $sharddbName -Edition $dbEdition -ServiceObjective $so
    } else {
        Write-Output "$sharddbName already exists"
    }
}

# Create new (or replace existing) shard map manager 
$ShardMapManager = New-ShardMapManager -UserName $userName -Password $password -SqlServerName $serverInstance -SqlDatabaseName $mgmtdb -ReplaceExisting $true

foreach ($range in $ranges) {

    # Create shard map
    $shardMapName = $shardMapNameStem + $range
    Write-Output "Creating $shardMapName"
    $ShardMap = New-RangeShardMap -KeyType $TKey -ShardMapManager $ShardMapManager -RangeShardMapName $shardMapName

    # Add shards
    $nRanges = $range
    for ($nRange = 0; $nRange -lt $nRanges; $nRange++) {
        $sharddbName = $sharddbStem + ($nRange + 1)
        Write-Output "    Adding shard in $shardMapName for $sharddbName"
        Add-Shard -ShardMap $ShardMap -SqlServerName $serverInstance -SqlDatabaseName $sharddbName

        # Create mapping on the shard
        #even distribution of ints
        $step = [uint32]($([uint32]::MaxValue) / $nRanges)
        $LowKey = $([int]::MinValue) + $nRange * $step
        $HighKey = $LowKey + $step
        if ($nRange -ne $nRanges - 1) {
            Write-Output "        Adding [$LowKey; $HighKey)"
            Add-RangeMapping -KeyType $TKey -RangeShardMap $ShardMap -RangeLow $LowKey -RangeHigh $HighKey -SqlServerName $serverInstance -SqlDatabaseName $sharddbName
            
        } else {
            Write-Output "        Adding [$LowKey; +inf)"
            Add-RangeMapping -KeyType $TKey -RangeShardMap $ShardMap -RangeLow $LowKey -HighIsMax $true -SqlServerName $serverInstance -SqlDatabaseName $sharddbName
        }
    }
}

# Create tables and procedures in all databases 
for ($nshard = 1; $nshard -le $shardsCount; $nshard++) {

    $sharddbName = $sharddbStem + $nshard
    $sqlFile = "$ScriptDir\TruncateTable.sql"
    Invoke-Sqlcmd -InputFile $sqlFile -ServerInstance $serverInstance -Database $sharddbName -Username $userNameFull -Password $password

    Write-Output "Ran SQL script in $sharddbName"
}
