<#
.SYNOPSIS
    Installs OrleansManagerSnapIn using InstallUtil.exe
#>
function Install-OrleansManagerSnapIn 
{
	[CmdletBinding()] # setup $PSCmdlet
	param()

	process
	{
		if ($PSCmdlet.ShouldProcess("Installing OrleansManagerSnapIn")) { 
			$env:windir\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe OrleansManagerSnapIn.dll
		}
		else {
			Write-Host "$env:windir\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe OrleansManagerSnapIn.dll"
		}
	}
}