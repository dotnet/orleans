Samples use Orleans Nuget packages available at https://www.nuget.org/packages?q=microsoft.orleans.

If you previously used an older version of Orleans, notably the 0.9 version, we recommend first to completely uninstall it.

We recommend running Azure Samples (AzureWebSample, TicTacToe) in elevated mode in Visual studio.


### TROUBLESHOOTING ###

You may need to execute the following commands to reserve static ports to Orleans or allow traffic through the firewall:
"netsh http add urlacl url=http://*:22222/ user=<DOMAIN\user>"
"netsh advfirewall firewall add rule name="Orleans" dir=in action=allow protocol=TCP localport=22222"

Here <DOMAIN\user> is the account for which reservation is being made, such the one reported by "whoami". Locaport number is the port to open for use. These show up in XML configuration files.

If you are greeted by an EntryPointNotFoundException, it may be due to not having .NET 4.5 installed or ASP.NET registered. Running the following will install the required feature and its parent features: 
"dism.exe /Online /Enable-Feature /all /FeatureName:IIS-ASPNET45".
