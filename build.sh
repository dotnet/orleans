#!/usr/bin/env bash
#
# Copyright (c) .NET Foundation and contributors. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#

# Set OFFLINE environment variable to build offline

set -e

SOURCE="${BASH_SOURCE[0]}"
while [ -h "$SOURCE" ]; do # resolve $SOURCE until the file is no longer a symlink
  DIR="$( cd -P "$( dirname "$SOURCE" )" && pwd )"
  SOURCE="$(readlink "$SOURCE")"
  [[ "$SOURCE" != /* ]] && SOURCE="$DIR/$SOURCE" # if $SOURCE was a relative symlink, we need to resolve it relative to the path where the symlink file was located
done
DIR="$( cd -P "$( dirname "$SOURCE" )" && pwd )"

DOCKER_IMAGENAME=microsoft/dotnet:2.1.300-sdk

# $args array may have empty elements in it.
# The easiest way to remove them is to cast to string and back to array.
# This will actually break quoted arguments, arguments like
# -test "hello world" will be broken into three arguments instead of two, as it should.
temp="${args[@]}"
args=($temp)

BUILD_COMMAND=/opt/code/run-build.sh "${args[@]}"

[ -z "$DOCKER_HOST_SHARE_DIR" ] && DOCKER_HOST_SHARE_DIR=$(pwd)

# Make container names CI-specific if we're running in CI
#  Jenkins
[ ! -z "$BUILD_TAG" ] && DOTNET_BUILD_CONTAINER_NAME="$BUILD_TAG"
#  VSO
[ ! -z "$BUILD_BUILDID" ] && DOTNET_BUILD_CONTAINER_NAME="$BUILD_BUILDID"

# Run the build in the container
echo "Launching build in Docker Container"
echo "Running command: $BUILD_COMMAND"
echo "Using code from: $DOCKER_HOST_SHARE_DIR"

docker run -t --rm --sig-proxy=true \
    --name docker-orleansbuild \
    -v $DOCKER_HOST_SHARE_DIR:/opt/code \
    -w /opt/code \
    $DOCKER_IMAGENAME $BUILD_COMMAND "$@"
