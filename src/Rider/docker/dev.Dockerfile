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

# .NET SDK — two channels installed side-by-side into a shared root, needed for two
# unrelated reasons:
# - 10.0: RunTestRunner.kt shells out to a bare "dotnet" on PATH to execute scenario
#   runs (issue #262's Run lens); a completely separate concern from publishServer's
#   dotnet (deliberately skipped in this container via ORG_GRADLE_PROJECT_lspServerBuildDir,
#   see devcontainer.json) and from Rider's own bundled CoreCLR backend (private runtime,
#   never on PATH). Without this, the Run lens's "dotnet test" click fails immediately
#   with "Cannot run program \"dotnet\": No such file or directory".
# - 8.0: needed so Rider's backend can fully restore/evaluate host .NET solutions opened
#   via a bind mount (e.g. Quickstart projects used for manual plugin testing), not for
#   building this plugin itself. Without it, NuGet restore never runs and Rider's project
#   model silently drops non-Compile items (e.g. .feature None/Content items with a
#   Generator), even though well-known globs like Compile still resolve fine.
# dotnet-install.sh installs each channel into its own versioned subdirectory under
# --install-dir, so running it twice against the same root is safe — installed via
# Microsoft's official script (rather than the apt feed, whose dotnet-sdk packages lag
# current releases and tie the version to whatever Debian/Ubuntu release backs the
# eclipse-temurin base image) into /usr/share/dotnet, symlinked onto PATH. One `ln -s`
# only — a second run-block doing its own `ln -s` to the same target would fail the
# build with "File exists" once this got consolidated.
RUN curl -fsSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:/usr/share/dotnet"
# Skip the first-run telemetry/welcome banner and first-time package cache population,
# both irrelevant in a throwaway dev container.
ENV DOTNET_NOLOGO=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# /mnt/wslg is bind-mounted in from the host (see devcontainer.json); the
# X11 socket actually lives at /mnt/wslg/.X11-unix, and this recreates the
# same /tmp/.X11-unix symlink WSLg itself uses — done here as a plain
# Linux-side operation rather than bind-mounting the symlink directly,
# which avoids a second layer of host-path translation.
RUN ln -s /mnt/wslg/.X11-unix /tmp/.X11-unix

WORKDIR /workspace