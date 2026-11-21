pushd %~dp0
git log -n 1
git --no-pager diff
dotnet run -c Release --framework net11.0 -- GrainStorage.AzureBlob.WriteState.Streaming
popd
