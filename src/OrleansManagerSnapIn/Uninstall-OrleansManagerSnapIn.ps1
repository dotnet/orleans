# TODO: this Script needs admin rights and .NET 4.5. Those preconditions need to be checked.

<#
.SYNOPSIS
    Uninstalls OrleansManagerSnapIn using InstallUtil.exe
#>
function Uninstall-OrleansManagerSnapIn 
{
	[CmdletBinding(SupportsShouldProcess = $true)]
	param()

	process
	{
		if ($PSCmdlet.ShouldProcess("Installing OrleansManagerSnapIn")) 
		{ 
			[string]$InstallUtil32 = Join-Path $env:windir "Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe" 
			Start-Process -FilePath $InstallUtil32 -ArgumentList "/u OrleansManagerSnapIn.dll" -NoNewWindow

			if([Environment]::Is64BitOperatingSystem)
			{
				[string]$InstallUtil64 = Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe" 
				Start-Process -FilePath $InstallUtil64 -ArgumentList "/u OrleansManagerSnapIn.dll" -NoNewWindow
			}
		}
	}
}

Uninstall-OrleansManagerSnapIn 