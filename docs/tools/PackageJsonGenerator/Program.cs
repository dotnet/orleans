// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using PackageJsonGenerator;

#if LAUNCH_DEBUGGER
if (!System.Diagnostics.Debugger.IsAttached)
{
    System.Diagnostics.Debugger.Launch();
}
#endif

var rootCommand = RootGenerateCommand.GetCommand();
rootCommand.Add(BatchGenerateCommand.GetCommand());
rootCommand.Add(CSharpFenceSyntaxCommand.GetCommand());

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
