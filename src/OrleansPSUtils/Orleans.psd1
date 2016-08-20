@{
	RootModule = 'OrleansPSUtils.dll'
	ModuleVersion = '$version$'
	GUID = 'afbcf490-ca4b-4039-9ffe-c6fec8a4f71b'
	Author = 'Microsoft Orleans Team'
	CompanyName = 'Microsoft'
	Copyright = 'Copyright Microsoft 2016'
	Description = 'Orleans Client Powershell module'
	PowerShellVersion = '3.0'
	CLRVersion = '4.0'
	FunctionsToExport = '*'
	VariablesToExport = '*'
	AliasesToExport = '*'

  PrivateData = @{
    PSData = @{
        Tags = @("Orleans", "Cloud-Computing", "Actor-Model", "Actors", "Distributed-Systems", "C#", ".NET")
        LicenseUri = 'https://github.com/dotnet/Orleans#license'
        ProjectUri = 'https://github.com/dotnet/orleans'
        IconUri = 'https://raw.githubusercontent.com/dotnet/orleans/gh-pages/assets/logo_128.png'
        ReleaseNotes = 'Initial Release $version$'
    } 
  }
}