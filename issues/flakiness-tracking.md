### Issue Tracking Test Flakiness

**Test:** `UnitTests.ActivationsLifeCycleTests.ActivationCollectorTests.NonReentrantGrainTimer_NoKeepAlive_Test`  
**Job:** [#19475926567](https://github.com/dotnet/orleans/actions/runs/19475926567/job/55764224944?pr=9659)  
**Commit Ref:** `4ab666dcb2f518104d5969aa98db9ebd1958f6ca`  

**Issue:** Intermittent test failure observed.  
**Error Message:** `Assert.Equal() Failure: Expected: 0, Actual: 1`  

This appears to be a flake, as previous runs have passed. The root cause should be investigated, possibly related to timing, cleanup, or resource handling issues.  

**Reported by:** ReubenBond  
**Date:** 2025-11-19 11:02:52 UTC