<#
/********************************************************
*                                                        *
*   © Microsoft. All rights reserved.                    *
*                                                        *
*********************************************************/

.SYNOPSIS
    Provides a set of methods to interact with
    Elastic Scale Shard Management functionality

.NOTES
    Author: Microsoft Azure SQL DB Elastic Scale team
    Last Updated: 9/16/2014
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Get current working director of script to reference .dlls
$ScriptDir = Split-Path -parent $MyInvocation.MyCommand.Path

# Add assemblies containing Shard Management related types
Add-Type -Path $ScriptDir\Microsoft.Azure.SqlDatabase.ElasticScale.Client.dll

<#
.SYNOPSIS
    Shard map manager enables one to add, modify, delete shard entries and ranges
#>
function New-ShardMapManager
{
    # Return either a Shard Map Manager object or null reference
    [OutputType([Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManager])]
    param (
        # User name for the shard map manager DB      
        [parameter(Mandatory=$true)]
        [String]$UserName,

        #Password for the shard map manager DB
        [parameter(Mandatory=$true)]
        [String]$Password,

        # Server name for the shard map manager DB
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,

        # DB name for the shard map manager
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName,

        # Application name             
        [parameter(Mandatory=$false)]
        [String]$AppName = "ESC_SEv1.0",
        
        [parameter()]
        [bool]$ReplaceExisting = $false
    )

    Write-Verbose "Creating Shard Map Manager in $SqlServerName.$SqlDatabaseName"

    # Reference assemblies containing Shard Management related types
    [Type]$ShardMapManagementFactoryType = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManagerFactory]

    # Build credentials for Shard Map Manager DB and Shard DBs
    $SmmConnectionString = "Server=$SqlServerName; Initial Catalog=$SqlDatabaseName; User ID=$UserName; Password=$Password; Application Name = $AppName;"

    # Create the Shard Map Manager
    if ($ReplaceExisting)
    {
        $CreateMode = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManagerCreateMode]::ReplaceExisting
    }
    else
    {
        $CreateMode = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManagerCreateMode]::KeepExisting
    }
 
    return $ShardMapManagementFactoryType::CreateSqlShardMapManager($SmmConnectionString, $CreateMode)
}

<#
.SYNOPSIS
    Shard map manager enables one to add, modify, delete shard entries and ranges
#>
function Get-ShardMapManager
{
    # Return either a Shard Map Manager object or null reference
    [OutputType([Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManager])]
    param (
        # User name for the shard map manager DB      
        [parameter(Mandatory=$true)]
        [String]$UserName,

        #Password for the shard map manager DB
        [parameter(Mandatory=$true)]
        [String]$Password,

        # Server name for the shard map manager DB
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,

        # DB name for the shard map manager
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName,

        # Application name             
        [parameter(Mandatory=$false)]
        [String]$AppName = "ESC_SEv1.0"
    )

    Write-Verbose "Getting Shard Map Manager in $SqlServerName.$SqlDatabaseName"

    
    # Reference assemblies containing Shard Management related types
    [Type]$ShardMapManagementFactoryType = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManagerFactory]

    # Build credentials for Shard Map Manager DB and Shard DBs
    $SmmConnectionString = "Server=$SqlServerName; Initial Catalog=$SqlDatabaseName; User ID=$UserName; Password=$Password; Application Name = $AppName;"

    # Check if a shard map manager exists on $SqlDatabaseName
    $LoadPolicy = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManagerLoadPolicy]::Lazy
    [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManager]$ShardMapManager = $null
    $Exists = $ShardMapManagementFactoryType::TryGetSqlShardMapManager($SmmConnectionString, $LoadPolicy, [ref]$ShardMapManager)
    
    return $ShardMapManager
   
}

<#
.SYNOPSIS
    Creates a new RangeShardMap<$KeyType>
#>
function New-RangeShardMap
{
    # Return a range shard map or null reference is the range shard map does not exist
    param 
    (
         # Type of range shard map
        [parameter(Mandatory=$true)]
        [Type]$KeyType,

        # Shard map manager object      
        [parameter(Mandatory=$true)]
        [System.Object]$ShardMapManager,

        # Name of the range 
        [parameter(Mandatory=$true)]
        [String]$RangeShardMapName
    )

    Write-Verbose "Creating Range Shard Map"
    
    # Get and cast necessary shard map management methods for a range shard map
    [Type]$ShardMapManagerType = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManager]
    $CreateRangeShardMapMethodGeneric = $ShardMapManagerType.GetMethod("CreateRangeShardMap")
    $CreateRangeShardMapMethodTyped = $CreateRangeShardMapMethodGeneric.MakeGenericMethod($KeyType)

    # Create the shard map
    $params = @($RangeShardMapName)
    return $CreateRangeShardMapMethodTyped.Invoke($ShardMapManager, $params)
}

<#
.SYNOPSIS
    Gets a RangeShardMap<$KeyType>
#>
function Get-RangeShardMap
{
    param 
    (
        # Type of range shard map
        [parameter(Mandatory=$true)]
        [Type]$KeyType,

        # Shard map manager object      
        [parameter(Mandatory=$true)]
        [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManager]$ShardMapManager,

        # Name of the range 
        [parameter(Mandatory=$true)]
        [String]$RangeShardMapName
    )
    
    # Get and cast necessary shard map management methods for a range shard map
    [Type]$ShardMapManagerType = [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMapManager]
    $TryGetRangeShardMapMethodGeneric = $ShardMapManagerType.GetMethod("TryGetRangeShardMap")
    $TryGetRangeShardMapMethodTyped = $TryGetRangeShardMapMethodGeneric.MakeGenericMethod($KeyType)

    # Check to see if $ShardMapName range shard map exists
    $params = @($RangeShardMapName, $null)
    $Exists = $TryGetRangeShardMapMethodTyped.Invoke($ShardMapManager, $params)
    $RangeShardMap = $params[1]

    return $RangeShardMap
}

<#
.SYNOPSIS
    Registers a particular database as a shard within a particular range shard map
#>
function Add-Shard
{
    param 
    (
        # Target shard map     
        [parameter(Mandatory=$true)]
        [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMap]$ShardMap,

        # SQL Server name for which the database is attributed to
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,

        # Database to be added to the shard map
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName
    )
    
    # Add new shard location to shard map
    $ShardLocation = New-Object Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardLocation($SqlServerName, $SqlDatabaseName)

    # Initialize reference for shard new shard
    [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.Shard]$ShardReference = $null

    Write-Verbose "`tChecking if shard $ShardLocation is registered with the shard map manager..."

    # Check to see if shard already exists 
    if ($ShardMap.TryGetShard($ShardLocation, [ref]$ShardReference))
    {
        Write-Verbose "`tShard $SqlDatabaseName already registered with the shard map manager"
        $InputShard = $ShardReference
    }
    else
    {
        Write-Verbose "`tShard $ShardLocation does not exist in the shard map manager, adding..."
        
        # Add $ShardName as a shard in the shard map manager
        $ShardMapReturn = $ShardMap.CreateShard($ShardLocation)

        Write-Verbose "`tShard $ShardLocation added to the shard map manager"
    }
}

<#
.SYNOPSIS
    Adds a low and high value for a particular shard to a range shard map
#>
function Add-RangeMapping
{
    param 
    (
         # Type of range shard map
        [parameter(Mandatory=$true)]
        [Type]$KeyType,

        [parameter(Mandatory=$true)]
        [System.Object]$RangeShardMap,

        [parameter(Mandatory=$true)]
        [int]$RangeLow,

        [parameter(Mandatory=$false)]
        [int]$RangeHigh,

        [parameter(Mandatory=$false)]
        [bool]$HighIsMax,
        
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,

        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName
    )
      
    # Add new shard location to range shard map
    $ShardLocation = New-Object Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardLocation($SqlServerName, $SqlDatabaseName)

    # Check if the range mapping already exists in the shard map manager    
    $InputShard = $rangeShardMap.GetShard($ShardLocation)

    if (!$HighIsMax) {
        $InputRange = New-Object Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.Range[$KeyType]($RangeLow, $RangeHigh)
    } else {
        $InputRange = New-Object Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.Range[$KeyType]($RangeLow)
    }
    Write-Verbose "`tChecking if range [$RangeLow, $RangeHigh) exists for $SqlDatabaseName..."

    $Mappings = $RangeShardMap.GetMappings($InputRange)
    if($Mappings.count -gt 0 -and $Mappings[0].Value -eq $InputRange)
    {
        Write-Verbose "`tRange [$RangeLow, $RangeHigh) already exists for $SqlDatabaseName"
    }
    else
    {
        Write-Verbose "`tRange [$RangeLow, $RangeHigh) for $SqlDatabaseName does not exist, adding..."
        $ShardReference = $rangeShardMap.CreateRangeMapping($InputRange, $InputShard)
        Write-Verbose "`tNew range [$RangeLow, $RangeHigh) for $SqlDatabaseName added to range shard map"
    }
}

<#
.SYNOPSIS
    Prints shard name as well as the shard's low and high shard range
#>
function Get-Mappings
{
    param 
    (   # Range map object     
        [parameter(Mandatory=$true)]
        [System.Object]$ShardMap
    )
    
    # Get mappings
    $mappings = $ShardMap.GetMappings()

    # Format them for PowerShell
    $formattedMappings = $mappings | foreach { 
        New-Object -TypeName PSObject -Property @{ 
            "Status" = $_.Status.ToString(); 
            "Value" = $_.Value; 
            "ShardLocation" = $_.Shard.Location; 
        } 
    }

    return $formattedMappings

}

<#
.SYNOPSIS
    Obtains the list shards for a particular shard map 
#>
function Get-Shards
{
    # Return an array of shards
    [OutputType([System.Object[]])]
    param 
    (
        # Shard map object      
        [parameter(Mandatory=$true)]
        [Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement.ShardMap]$ShardMap
    )

    # Get the list of shards
    return $ShardMap.GetShards()
}
