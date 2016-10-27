// Import the utility functionality.

import jobs.generation.Utilities;

def project = GithubProject
def branch = GithubBranchName
// Define build string
def buildString = '''call Build.cmd && Test.cmd'''

// Generate the builds for debug and release

[true, false].each { isPR ->
    ['netfx', 'netstandard-win'].each { platform ->
	    def newJobName = platform
		def newJob = job(Utilities.getFullJobName(project, newJobName, isPR)) {
			steps {
				batchFile("call Build.cmd ${platform} && Test.cmd ${platform}")
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
			Utilities.addArchival(newJob, 'Binaries/**')
			Utilities.addGithubPushTrigger(newJob)
		}
		else {
			Utilities.addGithubPRTriggerForBranch(newJob, branch, "${platform} Windows Debug and Release")
		}
	}
}
