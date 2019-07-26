# This script builds the sample for x64 Linux.
# It assumes that both the dotnet CLI and g++ compiler are available on the path.

SCRIPTPATH=$(readlink -f "$0")
BASEDIR=$(dirname $SCRIPTPATH)
SRCDIR=${BASEDIR}
OUTDIR=${BASEDIR}/bin/linux

# Make output directory, if needed
if [ ! -d "${OUTDIR}" ]; then
    mkdir -p ${OUTDIR}
fi

# Build managed component
echo Building Orleans TestClient Managed
dotnet publish --self-contained -r linux-x64 ${SRCDIR}/TestClient/TestClient.csproj -o ${OUTDIR}

# Build native component
g++ -o ${OUTDIR}/ClientCpp -D LINUX ${SRCDIR}/client.cpp -ldl -std=c++17 -stdlib=libc++  --debug