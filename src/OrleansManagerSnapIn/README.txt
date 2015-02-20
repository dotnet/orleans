
Installation 
============

1. start powershell with admin rights
2. register the Snap-In using InstallUtil
	* 32 bit Version
		C:\Windows\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe OrleansManagerSnapIn.dll
	* 64-bit Version
		C:\Windows\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe OrleansManagerSnapIn.dll
3. load the Snap-In
	Add-PSSnapin Orleans.Manager


Uninstallation 
==============

1. start powershell with admin rights
2. uninstall the Snap-In using InstallUtil
	* 32 bit Version
		C:\Windows\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe /u OrleansManagerSnapIn.dll
	* 64-bit Version
		C:\Windows\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe /u OrleansManagerSnapIn.dll
