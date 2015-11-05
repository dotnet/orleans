<#
/********************************************************
*                                                        *
*   © Microsoft. All rights reserved.                    *
*                                                        *
*********************************************************/

.SYNOPSIS
    Provides a set of methods to interact with the Elastic Scale Split-Merge service

    ===================================== IMPORTANT =====================================
    The following use of HTTP APIs is for convenience and illustration purposes only.
    They do not constitute the final development experience for the Split/Merge service 
    and are subject to change.
    ===================================== IMPORTANT =====================================

.NOTES
    Author: Microsoft SQL Elastic Scale team
    Last Updated: 8/22/2014   
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    Submits a split request to the Split-Merge service
#>
function Submit-SplitRequest
{
    param (
        [parameter(Mandatory=$true)]
        [string]$SplitMergeServiceEndpoint,

        [parameter(Mandatory=$true)]
        [string]$ShardMapManagerServerName,

        [parameter(Mandatory=$true)]
        [string]$ShardMapManagerDatabaseName,

        [parameter(Mandatory=$true)]
        [string]$TargetServerName,

        [parameter(Mandatory=$true)]
        [string]$TargetDatabaseName,

        [parameter(Mandatory=$true)]
        [string]$UserName,

        [parameter(Mandatory=$true)]
        [string]$Password,

        [parameter(Mandatory=$true)]
        [string]$ShardMapName,

        [parameter(Mandatory=$true)]
        [string]$ShardKeyType,

        # Use hexademical strings (e.g. 0x1234) to specify binary keys.
        [parameter(Mandatory=$true)]
        [string]$SplitValue,

        [parameter(Mandatory=$true)]
        [string]$SplitRangeLowKey,

        # Use $null for specifying positive infinity.
        [parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$SplitRangeHighKey,

        [string]$SplitBehavior = "MoveHigherRange", # or "MoveLowerRange"

        [string]$CertificateThumbprint = $null
    )

    Write-Verbose "Submitting split request to $SplitMergeServiceEndpoint"

    # ===================================== IMPORTANT =====================================
    # The following use of HTTP APIs is for convenience and illustration purposes only.
    # They do not constitute the final development experience for the Split/Merge service 
    # and are subject to change.
    # ===================================== IMPORTANT =====================================

    # Get web form	
    $certificate = Get-CertificateParameter $CertificateThumbprint
	$body = Invoke-WebRequest @certificate $SplitMergeServiceEndpoint -SessionVariable webSession
    $form = $body.Forms[0]

    # Populate form
    $form.Fields.OperationType = "split"
    $form.Fields.ShardMapManagerServerName = $ShardMapManagerServerName
    $form.Fields.ShardMapManagerDatabaseName = $ShardMapManagerDatabaseName
    $form.Fields.SqlDatabaseUserName = $UserName
    $form.Fields.SqlDatabasePassword = $Password
    $form.Fields.TargetServerName = $TargetServerName
    $form.Fields.TargetDatabaseName = $TargetDatabaseName
    $form.Fields.ShardMapName = $ShardMapName
    $form.Fields.BatchSize = 10
    $form.Fields.ShardKeyType = $ShardKeyType
    $form.Fields.SplitBehavior = $SplitBehavior
    $form.Fields.SplitKey = $SplitValue
    $form.Fields.SplitRangeLowKey = $SplitRangeLowKey
    if ($SplitRangeHighKey)
    {
        $form.Fields.SplitRangeHighKey = $SplitRangeHighKey
    }
    else
    {
        $form.Fields.SplitRangeHighKey = 'Null'
    }

    return Submit-RequestForm @certificate -SplitMergeServiceEndpoint $SplitMergeServiceEndpoint -Body $body -WebSession $webSession
}

<#
.SYNOPSIS
    Submits a merge request to the Split-Merge service
#>
function Submit-MergeRequest
{
    param (
        [parameter(Mandatory=$true)]
        [string]$SplitMergeServiceEndpoint,

        [parameter(Mandatory=$true)]
        [string]$ShardMapManagerServerName,

        [parameter(Mandatory=$true)]
        [string]$UserName,

        [parameter(Mandatory=$true)]
        [string]$Password,

        [parameter(Mandatory=$true)]
        [string]$ShardMapManagerDatabaseName,

        [parameter(Mandatory=$true)]
        [string]$ShardMapName,

        [parameter(Mandatory=$true)]
        [string]$ShardKeyType,

        # Use hexademical strings (e.g. 0x1234) to specify binary keys.
        [parameter(Mandatory=$true)]
        [string]$SourceRangeLowKey,

        # Use $null for specifying positive infinity.		
        [parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$SourceRangeHighKey,

        [parameter(Mandatory=$true)]
        [string]$TargetRangeLowKey,

        # Use $null for specifying positive infinity.
        [parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$TargetRangeHighKey,
		
        [string]$CertificateThumbprint = $null
    )

    Write-Verbose "Submitting split request to $SplitMergeServiceEndpoint"

    # ===================================== IMPORTANT =====================================
    # The following use of HTTP APIs is for convenience and illustration purposes only.
    # They do not constitute the final development experience for the Split/Merge service 
    # and are subject to change.
    # ===================================== IMPORTANT =====================================

    # Get web form
    $certificate = Get-CertificateParameter $CertificateThumbprint	
    $body = Invoke-WebRequest @certificate $SplitMergeServiceEndpoint -SessionVariable webSession
    $form = $body.Forms[0]

    # Populate form
    $form.Fields.OperationType = "merge"
    $form.Fields.ShardMapManagerServerName = $ShardMapManagerServerName
    $form.Fields.ShardMapManagerDatabaseName = $ShardMapManagerDatabaseName
    $form.Fields.SqlDatabaseUsername = $UserName
    $form.Fields.SqlDatabasePassword = $Password
    $form.Fields.ShardMapName = $ShardMapName
    $form.Fields.BatchSize = 10
    $form.Fields.ShardKeyType = $ShardKeyType
	$form.Fields.MergeSourceRangeLowKey = $SourceRangeLowKey
    if ($SourceRangeHighKey)
    {
        $form.Fields.MergeSourceRangeHighKey = $SourceRangeHighKey
    }
    else
    {
        $form.Fields.MergeSourceRangeHighKey = 'Null'
    }
    $form.Fields.MergeTargetRangeLowKey = $TargetRangeLowKey
    if ($TargetRangeHighKey)
    {
        $form.Fields.MergeTargetRangeHighKey = $TargetRangeHighKey
    }
    else
    {
        $form.Fields.MergeTargetRangeHighKey = 'Null'
    }

    return Submit-RequestForm @certificate -SplitMergeServiceEndpoint $SplitMergeServiceEndpoint -Body $body -WebSession $webSession
}

<#
.SYNOPSIS
    Submits a cancel request to the Split-Merge service
#>
function Submit-CancelRequest
{
    param (
        [parameter(Mandatory=$true)]
        [string]$SplitMergeServiceEndpoint,

        [parameter(Mandatory=$true)]
        [string]$OperationId,
		
        [string]$CertificateThumbprint = $null
    )

    Write-Verbose "Submitting cancel request to $SplitMergeServiceEndpoint"

    # ===================================== IMPORTANT =====================================
    # The following use of HTTP APIs is for convenience and illustration purposes only.
    # They do not constitute the final development experience for the Split/Merge service 
    # and are subject to change.
    # ===================================== IMPORTANT =====================================

    # Get web form
    $certificate = Get-CertificateParameter $CertificateThumbprint
    $body = Invoke-WebRequest @certificate $SplitMergeServiceEndpoint -SessionVariable webSession
    $form = $body.Forms[0]

    # Populate form
    $form.Fields.OperationType = "cancel"
    $form.Fields.OperationId = $OperationId

    # Send the form
    $postResponseString = Invoke-RestMethod @certificate -Uri ($SplitMergeServiceEndpoint + $body.Forms[0].Action) -Method Post -Body $body -WebSession $webSession
    Write-Verbose "Got response $postResponseString"
}

function Submit-RequestForm
{
    param (
        [parameter(Mandatory=$true)]
        [string]$SplitMergeServiceEndpoint,

        [parameter(Mandatory=$true)]
        [Microsoft.PowerShell.Commands.HtmlWebResponseObject]$Body,
		
        [parameter(Mandatory=$true)]
        [Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,

        [string]$CertificateThumbprint = $null
    )
    
    # ===================================== IMPORTANT =====================================
    # The following use of HTTP APIs is for convenience and illustration purposes only.
    # They do not constitute the final development experience for the Split/Merge service 
    # and are subject to change.
    # ===================================== IMPORTANT =====================================

    # Send the form
    $certificate = Get-CertificateParameter $CertificateThumbprint
    $postResponseString = Invoke-RestMethod @certificate -Uri ($SplitMergeServiceEndpoint + $Body.Forms[0].Action) -Method Post -Body $Body -WebSession $WebSession
    Write-Verbose "Got response $postResponseString"

    # Get the operation id
    $postResponseJson = ConvertFrom-Json $postResponseString
    if ($($postResponseJson | Get-Member -Name OperationId) -ne $null)
    {
        return $postResponseJson.OperationId
    }
    
    if ($($postResponseJson | Get-Member -Name Details) -ne $null)
    {
        throw "Failure to submit request: $postResponseJson.Details"
    }
}

<#
.SYNOPSIS
    Gets the status of a request from the Split-Merge service. Returns it in Json format.
#>
function Get-SplitMergeRequestStatus
{
    param (
        [parameter(Mandatory=$true)]
        [string]$SplitMergeServiceEndpoint,

        [parameter(Mandatory=$true)]
        [string]$OperationId,
		
        [string]$CertificateThumbprint = $null
    )

    Write-Verbose "Getting request status from $SplitMergeServiceEndpoint"

    # ===================================== IMPORTANT =====================================
    # The following use of HTTP APIs is for convenience and illustration purposes only.
    # They do not constitute the final development experience for the Split/Merge service 
    # and are subject to change.
    # ===================================== IMPORTANT =====================================

    # Send the form
    $certificate = Get-CertificateParameter $CertificateThumbprint
    $statusString = Invoke-RestMethod @certificate -Uri ($SplitMergeServiceEndpoint + "/api/splitmerge/getstatus?operationid=" + $OperationId) -Method Get
    $statusJson = ConvertFrom-Json $statusString

    return $statusJson
}

<#
.SYNOPSIS
    Waits for the request to complete, and writes output as it becomes available
#>
function Wait-SplitMergeRequest
{
    param (
        [parameter(Mandatory=$true)]
        [string]$SplitMergeServiceEndpoint,
    
        [Parameter(Mandatory=$true)]
        [Guid]$OperationId,
        
        [string]$CertificateThumbprint = $null
    )

    Write-Output 'Polling request status. Press Ctrl-C to end'

    $previousOutput = ""
    while ($true)
    {
        # ===================================== IMPORTANT =====================================
        # The following use of HTTP APIs is for convenience and illustration purposes only.
        # They do not constitute the final development experience for the Split/Merge service 
        # and are subject to change.
        # ===================================== IMPORTANT =====================================
    
        # Get the status
        $statusJson = Get-SplitMergeRequestStatus -SplitMergeServiceEndpoint $SplitMergeServiceEndpoint -OperationId $OperationId -CertificateThumbprint $CertificateThumbprint
    
        # Write to output, if it is different from the previous iteration
        $output = "Progress: $($statusJson.progress)% | Status: $($statusJson.status) | Details: $($statusJson.details)"
        if ($output -ne $previousOutput)
        {
            Write-Host $output
        }
        $previousOutput = $output

        # If it is completed then end.
        if ($statusJson.status -eq 'Succeeded' -or $statusJson.status -eq 'Canceled')
        {
            # Completed successfully
            return
        } 
        elseif ($statusJson.status -eq 'Failed')
        {
            # Completed unsuccessfully
            throw $statusJson.Details
        }
        
        Start-Sleep -Milliseconds 500
    }
}

<#
.SYNOPSIS
    Packs the optional client certificate arguments for Invoke-* cmdlets.
#>
function Get-CertificateParameter
{
    param (
        [parameter(Mandatory=$false)]
        [string]$CertificateThumbprint = $null	
    )
	
	$result = @{}
	if ($CertificateThumbprint) {
	  $result = @{ 'CertificateThumbprint' = $CertificateThumbprint }
	}
	
	return $result
}
