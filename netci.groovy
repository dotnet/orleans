// Import the utility functionality.

import jobs.generation.Utilities;

def project = 'dotnet/orleans'
// Define build string
def buildString = '''call "C:\\Program Files (x86)\\Microsoft Visual Studio 14.0\\Common7\\Tools\\VsDevCmd.bat" && Build.cmd && Test.cmd'''

// Generate the builds for debug and release

def commitJob = job(Utilities.getFullJobName(project, '', false)) {
  label('windows')
  steps {
    batchFile(buildString)
  }
}
             
def PRJob = job(Utilities.getFullJobName(project, '', true)) {
  label('windows')
  steps {
    batchFile(buildString)
  }
}

Utilities.addScm(commitJob, project)
Utilities.addStandardOptions(commitJob)
Utilities.addStandardNonPRParameters(commitJob)
Utilities.addGithubPushTrigger(commitJob)
Utilities.addArchival(commitJob, 'Binaries/**')

Utilities.addPRTestSCM(PRJob, project)
Utilities.addStandardOptions(PRJob)
Utilities.addStandardPRParameters(PRJob, project)
Utilities.addGithubPRTrigger(PRJob, 'Debug and Release')