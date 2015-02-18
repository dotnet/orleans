' This script parses a logfile from a ChirperNetworkDriver file to find data points (chirps/sec).
' It outputs a file with the raw data points' values, seperated by newlines.

Dim rxpOne, rxpTwo, MaxCount

If WScript.Arguments.Count = 2 Then
  MaxCount = CInt(WScript.Arguments(1))
End If

' Strings with 'sec': everything from the character before through the end of the line.
' Matches everything but the start of:  965/sec: 650000 in 10363ms.  Pipeline contains 497 items.
Set rxpOne = new RegExp
rxpOne.Global = True
rxpOne.Multiline = False
rxpOne.Pattern = ".sec.*$"

' The entirety of any line that begins with anything other than a number
Set rxpTwo = new RegExp
rxpTwo.Global = True
rxpTwo.Multiline = False
rxpTwo.Pattern = "^[^\d].*$"

Dim oShell, fso, file, objExec

' convert from unicode to ascii
Set oShell = WScript.CreateObject ("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
Set objExec = oShell.Exec ("cmd /C type " + WScript.Arguments(0) + " > .\temp.txt")

Do Until objExec.Status
  WScript.Sleep 100
Loop

set file = fso.OpenTextFile(".\temp.txt")

Dim count
Do While (maxCount <= 0 OR count < maxCount) And Not file.AtEndOfStream 
  ' For each line in the input file
  inp = file.ReadLine()
  ' Find our data points, and strip out everything but the numbers
  stepOne = rxpOne.Replace(inp, "")
  ' Assume that anything starting with a number is good, and strip out everything else
  stepTwo = rxpTwo.Replace(stepOne, "")
  ' If there's anything remaining, it is a data point.  Echo the line (it will be captured by the calling script)
  If Len(stepTwo) > 0 Then
    WScript.Echo stepTwo
    count = count + 1
  End If
Loop
file.Close

Dim fileToDelete
Set fileToDelete = fso.GetFile(".\temp.txt")
fileToDelete.Delete