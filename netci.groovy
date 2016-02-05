// Import the utility functionality.

import jobs.generation.Utilities;

def project = GithubProject
// Define build string
def buildString = '''call "C:\\Program Files (x86)\\Microsoft Visual Studio 14.0\\Common7\\Tools\\VsDevCmd.bat" && Build.cmd && Test.cmd'''

// Generate the builds for debug and release

[true, false].each { isPR ->
    def newJob = job(Utilities.getFullJobName(project, '', isPR)) {
        label('windows')
        steps {
            batchFile(buildString)
        }
    }
    
    Utilities.simpleInnerLoopJobSetup(newJob, project, isPR, 'Debug and Release')
    // Archive only on commit builds.
    if (!isPR) {
        Utilities.addArchival(newJob, 'Binaries/**')
    }
    
    newJob.with {
        publishers {
            archiveXUnit {
                xUnitDotNET {
                    pattern('src/TestResults/xUnit-Results.xml')
                    skipNoTestFiles(true)
                    failIfNotNew(true)
                    deleteOutputFiles(true)
                    stopProcessingIfError(true)
                }
                
                failedThresholds {
                    unstable(0)
                    unstableNew(0)
                    failure(0)
                    failureNew(0)
                }
                skippedThresholds {
                    unstable(100)
                    unstableNew(100)
                    failure(100)
                    failureNew(100)
                }
                thresholdMode(ThresholdMode.PERCENT)
                timeMargin(3000)
            }
        }
    }
}