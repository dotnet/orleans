// Import the utility functionality.

import jobs.generation.Utilities;

def project = GithubProject
def branch = GithubBranchName

[true, false].each { isPR ->
    ['netfx', 'netstandard-win'].each { platform ->
        ['bvt', 'functional'].each { testCategory ->
            def newJobName = "${platform}-${testCategory}"
            def testScript = "Test.cmd";
            if (testCategory == 'functional') { testScript = "TestAll.cmd" }

            def newJob = job(Utilities.getFullJobName(project, newJobName, isPR)) {
                steps {
                    batchFile("call Build.cmd ${platform} && SET OrleansDataConnectionString= && ${testScript} ${platform}")
                }
            }
            
            if (platform == 'netfx') {
                Utilities.setMachineAffinity(newJob, 'Windows_NT', 'latest-or-auto')
            } else {
                // need to use a machine that has .NET 4.6.2 installed in the system
                Utilities.setMachineAffinity(newJob, 'Windows_NT', '20161027')
            }
            
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
        }
    }
}
