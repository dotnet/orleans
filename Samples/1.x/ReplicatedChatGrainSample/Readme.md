# How to run this sample in Visual Studio with the Azure Simulator

## Before Compiling: Build the needed Orleans packages

If you are trying to use a version of Orleans that is not yet released, first run `Build.cmd` from the root directory of the repository.

Note: If you need to reinstall packages (for example, after making changes to the Orleans runtime and rebuilding the packages), just manually delete all Orleans packages from `(rootfolder)/Samples/{specific-sample}/packages/` and re-run `Build.cmd`. You might sometimes also need to clean up the NuGet cache folder.

You can then compile as usual, build solution.

## Before Running: Configure the Azure Simulator

Right-click `Deployment1`, choose `Properties`, then go to the `Web` settings and make sure to choose `Express` settings.
Do the same with `Deployment2` and `Deployment3` projects.



# To run a single deployment

1.	Right-click `Deployment1` and choose `Set as Start-Up project`
2.	Hit the `Start` button or `F5`


# To run all 3 deployments

1.	Important: Make sure you have started Visual Studio in Administrator Mode. For some reason, emulation of multiple websites does not work otherwise. If you have started the emulator in non-admin mode, it is possible you need to shut it down before restarting it in admin mode.
2.	Right-click the solution and choose `Set Start-Up Projects…`
3.	Choose multiple startup projects and select the `Start` action for projects `Deployment1`, `Deployment2` and `Deployment3`
4.	Hit the `Start` button or `F5`


# Known Issues

Sometimes TimeoutException is thrown, especially when starting in Azure Simulator the first time.  In our experience, these exceptions go away if you just try again (perhaps the simulator starts faster the second time around).