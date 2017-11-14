// Import the utility functionality.

import jobs.generation.Utilities;

def project = GithubProject
def branch = GithubBranchName

def platformList = ['Linux', 'Windows_NT']

platformList.each { platform ->
    if (platform == 'Linux') {
        [true, false].each { isPR ->
            def configuration = 'Release'
            def osUsedForMachineAffinity = 'Ubuntu16.04';
            def buildCommand = "./build.sh --configuration ${configuration} --targets Default"

            def newJob = job(Utilities.getFullJobName(project, platform, isPR)) {
                steps {
                    shell(buildCommand)
                }
            }

            Utilities.setMachineAffinity(newJob, osUsedForMachineAffinity, 'latest-or-auto')
            Utilities.standardJobSetup(newJob, project, isPR, "*/${branch}")

            if (!isPR) {
                Utilities.addGithubPushTrigger(newJob)
            }
            else {
                Utilities.addGithubPRTriggerForBranch(newJob, branch, "${osUsedForMachineAffinity} ${configuration} Build")
            }
        }
    }
    else if (platform == 'Windows_NT') {
        [true, false].each { isPR ->
            ['bvt', 'functional'].each { testCategory ->
                def newJobName = "${testCategory}"
                def testScript = "Test.cmd";
                if (testCategory == 'functional') { testScript = "TestAll.cmd" }

                def newJob = job(Utilities.getFullJobName(project, newJobName, isPR)) {
                    steps {
                        batchFile("SET BuildConfiguration=Release&&set VersionSuffix=ci-%BUILD_ID%&& call Build.cmd && SET OrleansDataConnectionString= && ${testScript}")
                    }
                }

                // need to use a machine that has .NET 4.6.2 installed in the system for now.
                Utilities.setMachineAffinity(newJob, 'Windows_NT', 'latest-or-auto')

                Utilities.standardJobSetup(newJob, project, isPR, "*/${branch}")
                Utilities.addXUnitDotNETResults(newJob, '**/xUnit-Results*.xml')
                // Archive only on commit builds.
                if (!isPR) {
                    if (testCategory == 'bvt') {
                        // no reason to archive for every kind of test run
                        Utilities.addArchival(newJob, '**/Binaries/**')
                    }
                    Utilities.addGithubPushTrigger(newJob)
                }
                else {
                    Utilities.addGithubPRTriggerForBranch(newJob, branch, newJobName)
                }

                // args: archive *.binlog, don't exclude anything, don't fail if there are no files, archive in case of failure too
                Utilities.addArchival(newJob, '*.binlog', '', true, false)
            }
        }
    }
}
