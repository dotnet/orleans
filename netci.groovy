// Import the utility functionality.

import jobs.generation.Utilities;

def project = GithubProject
def branch = GithubBranchName
// Define build string
def buildString = '''call Build.cmd && Test.cmd'''

// Generate the builds for debug and release

[true, false].each { isPR ->
    def newJob = job(Utilities.getFullJobName(project, '', isPR)) {
        steps {
            batchFile(buildString)
        }
    }
    
    Utilities.setMachineAffinity(newJob, 'Windows_NT', 'latest-or-auto')
    Utilities.standardJobSetup(newJob, project, isPR, "*/${branch}")
    Utilities.addXUnitDotNETResults(newJob, '**/xUnit-Results*.xml')
    // Archive only on commit builds.
    if (!isPR) {
        Utilities.addArchival(newJob, 'Binaries/**')
        Utilities.addGithubPushTrigger(newJob)
    }
    else {
        Utilities.addGithubPRTriggerForBranch(newJob, branch, "Windows Debug and Release")
    }
}
