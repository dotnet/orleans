#
# Copyright (c) .NET Foundation and contributors. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
#

# Dockerfile that creates a container suitable to build dotnet-cli
FROM microsoft/dotnet-buildtools-prereqs:fedora23_prereqs

# Install tools used by the VSO build automation.
RUN dnf install -y findutils && \
    dnf clean all

# Upgrade NSS, used for SSL, to avoid NuGet restore timeouts.
RUN dnf upgrade -y nss
RUN dnf clean all

# Setup User to match Host User, and give superuser permissions
ARG USER_ID=0
RUN useradd -m code_executor -u ${USER_ID} -g wheel
RUN echo 'code_executor ALL=(ALL) NOPASSWD:ALL' >> /etc/sudoers

# With the User Change, we need to change permissions on these directories
RUN chmod -R a+rwx /usr/local
RUN chmod -R a+rwx /home

# Set user to the one we just created
USER ${USER_ID}

# Set working directory
WORKDIR /opt/code
