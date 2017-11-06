#!/usr/bin/env bash
#
# Copyright (c) .NET Foundation and contributors. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

current_userid=$(id -u)
if [ $current_userid -ne 0 ]; then
    echo "$(basename "$0") uninstallation script requires superuser privileges to run"
    exit 1
fi

host_package_name="dotnet-host"
aspnetcore_package_store_package_name="aspnetcore-store*"

remove_all(){
    yum remove -y $host_package_name
    yum remove -y $aspnetcore_package_store_package_name
}

is_dotnet_host_installed(){
    local out="$(yum list installed | grep $host_package_name)"
    [ -z "$out" ]
}

is_dotnet_host_installed
[ "$?" -eq 0 ] && echo "Unable to find dotnet installation to remove." >&2 \
    && exit 0

remove_all
[ "$?" -ne 0 ] && echo "Failed to remove dotnet packages." >&2 && exit 1

is_dotnet_host_installed
[ "$?" -ne 0 ] && \
    echo "dotnet package removal succeeded but appear to still be installed. Please file an issue at https://github.com/dotnet/cli" >&2 && \
    exit 1

echo "dotnet package removal succeeded." >&2
exit 0
