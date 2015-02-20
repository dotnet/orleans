<#
.SYNOPSIS
    Uninstalls OrleansManagerSnapIn using InstallUtil.exe
#>
function Uninstall-OrleansManagerSnapIn 
{
	[CmdletBinding()] # setup $PSCmdlet
	param()

	process
	{
		if ($PSCmdlet.ShouldProcess("Uninstalling OrleansManagerSnapIn")) { 
			$env:windir\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe /u OrleansManagerSnapIn.dll
		}
	}
}