@echo off
powershell -ExecutionPolicy ByPass -NoProfile -command "& """%~dp0update-packagesource.ps1""" %*"
