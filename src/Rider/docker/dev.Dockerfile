# src/Rider/docker/dev.Dockerfile
FROM eclipse-temurin:21-jdk

RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    ca-certificates \
    curl \
    unzip \
    libfreetype6 \
    fontconfig \
    libxext6 \
    libxrender1 \
    libxtst6 \
    libxi6 \
    libxrandr2 \
    libicu-dev \
    libssl-dev \
    zlib1g \
    && rm -rf /var/lib/apt/lists/*
# libxext6/libxrender1/libxtst6/libxi6/libxrandr2: the JetBrains Runtime's AWT/Swing
# toolkit dlopens these X11 client libraries at startup (java.awt.Toolkit.initStatic ->
# libawt_xawt.so) even though only libXext actually failed first — X11 apps typically
# need this whole cluster, so they're added together rather than one apt-get round trip
# per missing .so.
#
# libicu-dev/libssl-dev/zlib1g: Rider always spawns its own .NET (CoreCLR) backend
# process alongside the JVM frontend, regardless of what this plugin does — a
# JDK-only base image has none of the native libs that backend's CoreCLR needs to
# start. Best-effort fix for a "Rider host ... exited with exit code 134" (SIGABRT)
# crash with no further detail in idea.log; if it still crashes after this, check for
# a dedicated backend log under build/idea-sandbox/<RD version>/system/log/ for the
# actual abort reason.

# Only used once, to bootstrap ./gradlew (see CONTRIBUTING.md) — the wrapper jar isn't
# committed since it's a binary blob; everything after the first `gradle wrapper` run
# goes through the checked-in wrapper script instead of this system install.
ARG GRADLE_VERSION=8.10
RUN curl -fsSL -o /tmp/gradle.zip "https://services.gradle.org/distributions/gradle-${GRADLE_VERSION}-bin.zip" \
    && unzip -q /tmp/gradle.zip -d /opt \
    && rm /tmp/gradle.zip \
    && ln -s /opt/gradle-${GRADLE_VERSION}/bin/gradle /usr/local/bin/gradle

# /mnt/wslg is bind-mounted in from the host (see devcontainer.json); the
# X11 socket actually lives at /mnt/wslg/.X11-unix, and this recreates the
# same /tmp/.X11-unix symlink WSLg itself uses — done here as a plain
# Linux-side operation rather than bind-mounting the symlink directly,
# which avoids a second layer of host-path translation.
RUN ln -s /mnt/wslg/.X11-unix /tmp/.X11-unix

# .NET SDK for `dotnet test` — RunTestRunner.kt shells out to a bare "dotnet" on PATH to
# execute scenario runs (issue #262's Run lens); this is a completely separate concern
# from publishServer's dotnet (deliberately skipped in this container via
# ORG_GRADLE_PROJECT_lspServerBuildDir, see devcontainer.json) and from Rider's own bundled
# CoreCLR backend (private runtime, never on PATH). Without this, the Run lens's "dotnet
# test" click fails immediately with "Cannot run program \"dotnet\": No such file or
# directory". Installed via Microsoft's official script rather than apt (Ubuntu's dotnet-sdk
# packages lag current releases) into /usr/share/dotnet, symlinked onto PATH.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet

WORKDIR /workspace