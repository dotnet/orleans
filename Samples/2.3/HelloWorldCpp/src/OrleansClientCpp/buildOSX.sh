# This script builds the sample for x64 OSX.
# It assumes that both the dotnet CLI and clang++ compiler are available on the path.
# If you want to use g++ just change the line 21

BASEDIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
SRCDIR=${BASEDIR}
OUTDIR=${BASEDIR}/bin/osx

# Make output directory, if needed
if [ ! -d "${OUTDIR}" ]; then
    mkdir -p ${OUTDIR}
fi

# Build managed component
echo Building Orleans TestClient Managed
dotnet publish --self-contained -r osx-x64 ${SRCDIR}/TestClient/TestClient.csproj -o ${OUTDIR}

# Build native component
# -D both LINUX and OSX since most LINUX code paths apply to OSX also
clang++ -o ${OUTDIR}/ClientCpp -D LINUX -D OSX ${SRCDIR}/client.cpp -ldl -std=c++17 -stdlib=libc++  --debug