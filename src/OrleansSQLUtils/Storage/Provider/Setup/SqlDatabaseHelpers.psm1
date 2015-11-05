<#
/********************************************************
*                                                        *
*   © Microsoft. All rights reserved.                    *
*                                                        *
*********************************************************/

.SYNOPSIS
    Provides a set of methods to assist with Azure SQL DB functionality

.NOTES
    Author: Microsoft Azure SQL DB Elastic Scale team
    Last Updated: 9/16/2014   
#>

<#
.SYNOPSIS
    Creates a new SQL DB 
#>
function New-SqlDatabase
{
    param 
    (
        # User name for the shard map manager DB      
        [parameter(Mandatory=$true)]
        [String]$UserName,

        # Password for the shard map manager DB
        [parameter(Mandatory=$true)]
        [String]$Password,
        
        # Server for which the $SqlDatabaseName will be attributed to
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,
        
        # Name of the new database to be created              
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName,

        # Name of the new database to be created              
        [parameter(Mandatory=$false)]
        [String]$Edition = "STANDARD"
    )

    Write-Verbose "Creating database $SqlServerName.$SqlDatabaseName"

    # Determine if we are connecting to SQL box or Azure SQL DB
    $SqlQuerySelectEngineEdition = "SELECT SERVERPROPERTY('EngineEdition')"
    $EngineEdition = Invoke-SqlScalar -UserName $Username -Password $Password -SqlServerName $SqlServerName -SqlDatabaseName "Master" -DbQuery $SqlQuerySelectEngineEdition

    if ($EngineEdition -eq 5)
    {
        # Azure SQL DB

        # Function to determine when the database creation has completed
        function IsDatabaseOnline
        {
            $DbState = Invoke-SqlScalar -UserName $Username -Password $Password -SqlServerName $SqlServerName -SqlDatabaseName "Master" -DbQuery "select state from sys.databases where name='$SqlDatabaseName'"
            return $DbState -eq 0 # ONLINE
        }

        # Construct and execute query to create new DB
        $SqlQueryCreateNewDb = "CREATE DATABASE $SqlDatabaseName (EDITION='$Edition')"
        $null = Invoke-SqlScalar -UserName $Username -Password $Password -SqlServerName $SqlServerName -SqlDatabaseName "Master" -DbQuery $SqlQueryCreateNewDb

        # Create check in the case the DB creation fails
        $DbCreationTimeoutSeconds = 600
        $DbCreationTime = 0
        $DbCreationSleepSeconds = 1

        # Entry while loop until new DB is created
        While (-not $(IsDatabaseOnline))
        {
            Write-Verbose "`tWait for $SqlDatabaseName to be created...."
            Start-Sleep -s $DbCreationSleepSeconds
        
            #If DB is not created with the specified threshold exit the loop
            if($DbCreationTime > $DbCreationTimeoutSeconds)
            {
                throw "Failed to create database within allotted time"
            }
            $DbCreationTime += $DbCreationSleepSeconds
        }
    }
    else
    {
        # Other edition of SQL DB
        $SqlQueryCreateNewDb = "CREATE DATABASE $SqlDatabaseName"
        return Invoke-SqlScalar -UserName $Username -Password $Password -SqlServerName $SqlServerName -SqlDatabaseName "Master" -DbQuery $SqlQueryCreateNewDb
    }
}

<#
.SYNOPSIS
    Executes a T-SQL command
#>
function Invoke-SqlScalar
{
    param 
    (
        # User name for the DB      
        [parameter(Mandatory=$true)]
        [String]$UserName,

        # Password for the DB
        [parameter(Mandatory=$true)]
        [String]$Password,
        
        # Server name for the DB
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,
        
        # Name of DB to query              
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName,

        # Query to execute             
        [parameter(Mandatory=$true)]
        [String]$DbQuery
    )
    
    # Create SQL connection to $SqlDatabaseName
    $DbConnection = New-Object System.Data.SqlClient.SqlConnection
    $DbConnectionString = "Server = $SqlServerName; Database = $SqlDatabaseName; User ID=$UserName; Password=$Password; Application Name = $AppName;"
    $DbConnection.ConnectionString = $DbConnectionString
    $DbConnection.Open()

    # Create SQL command for $TargetSqlDatabaseName
    $DbCommand = New-Object System.Data.SQLClient.SQLCommand 
    $DbCommand.Connection = $DbConnection
    $DbCommand.CommandText = $DbQuery

    # Execute the user defined $DbQuery
    $ScalarResult = $DbCommand.ExecuteScalar() 

    $DbConnection.Close()

    return($ScalarResult)
}

<#
.SYNOPSIS
    Detects whether or not a particular database exists 
#>
function Test-SqlDatabase
{
    param 
    (
        # User name for the shard map manager DB      
        [parameter(Mandatory=$true)]
        [String]$UserName,

        #Password for the shard map manager DB
        [parameter(Mandatory=$true)]
        [String]$Password,
        
        # Server name for the DB
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,
        
        # Name of DB in question              
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName
    )

    Write-Verbose "Checking if database $ServerName.$SqlDatabaseName exists"

    # Construct query to check if database already exists
    $SqlQueryCheckDbExists = "SELECT count(*) FROM [sys].[databases] WHERE name = '$SqlDatabaseName'"

    $ReturnDbCount = Invoke-SqlScalar -UserName $Username -Password $Password -SqlServerName $SqlServerName -SqlDatabaseName "Master" -DbQuery $SqlQueryCheckDbExists

    # ReturnDbCount will be greater than zero if the database exists
    if($ReturnDbCount -gt 0)
    {
        return($True)
    }
    else
    {
        return($False)
    }
}

<#
.SYNOPSIS
    Alter the DB service tier from to $TargetDbEdition 
#>
function Edit-SqlDatabaseEdition
{
    param 
    (
        # User name for the shard DB      
        [parameter(Mandatory=$true)]
        [String]$UserName,

        # Password for the shard DB
        [parameter(Mandatory=$true)]
        [String]$Password,
        
        # Server name for the shard DB
        [parameter(Mandatory=$true)]
        [String]$SqlServerName,

        # Target database     
        [parameter(Mandatory=$true)]
        [String]$SqlDatabaseName,

        # Target edition      
        [parameter(Mandatory=$true)]
        [String]$TargetDbEdition
    )

    # Construct query to obtain the maximum size for the database
    $SqlQueryAlterEdition = "ALTER DATABASE $SqlDatabaseName MODIFY (EDITION='$TargetDbEdition')"

    # Execute query to alter the service tier of the specified database
    $DbAge = Invoke-SqlScalar -UserName $Username -Password $Password -SqlServerName $SqlServerName -SqlDatabaseName "Master" -DbQuery $SqlQueryAlterEdition
}