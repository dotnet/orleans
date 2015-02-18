@echo OFF
set _Server=%1
set _Usr=%2
set _pass=%3
set _Script=%4

echo %_Server% 
echo %_Script%
echo %2

if [%1] equ [] if [%2] equ [] goto one
REM if [%2] equ [] goto one
if [%1] neq [] if [%2] neq [] if [%3] neq [] if [%4] equ [] goto four
if [%1] neq [] if [%2] neq [] if [%3] equ [] if [%4] equ [] goto three
if [%1] neq [] if [%2] neq [] if [%3] neq [] if [%4] neq [] goto two

:one
echo value missing

echo example StartProcessWRS.cmd "server name" "script to run"
goto end

:two

winrs -r:%1 -u:%2 -p:%3 %4
goto end

:three
echo three
winrs -r:%1 %2
goto end

:four
echo four
winrs -r:%1 -d:%2 %3
goto end

:end
