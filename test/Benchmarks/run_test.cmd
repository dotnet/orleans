pushd %~dp0
git log -n 1
git --no-pager diff
dotnet run -c Release -- ConcurrentPing
popd