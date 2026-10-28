# The dedicated game server: one raid per process. When a raid closes the server exits, and the container's
# restart policy starts a fresh one for the next raid.
#
# The build context is a Linux Dedicated Server build of the Unity project, as CI makes it with GameCI
# (.github/workflows/server.yml) or as Unity's Build Profiles window makes it (Linux Server):
#
#   docker build -f unity/server.Dockerfile -t raidzero-gameserver build/StandaloneLinux64
#
# Settings come from the environment: RAIDZERO_BACKEND_URL and RAIDZERO_SERVER_TOKEN (required), and
# RAIDZERO_OUTBOX_DIR, which points into the /data volume so undelivered raid results survive a restart.
# Command-line extras, such as --raid-minutes 5, go after the image name in docker run.
FROM ubuntu:24.04

# Certificates for an https backend URL; the player needs nothing else beyond the base image.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --create-home --uid 10001 raidzero \
 && mkdir -p /data \
 && chown raidzero /data

WORKDIR /app
COPY --chown=raidzero . .
RUN chmod +x RaidZeroServer.x86_64

USER raidzero
# The commit the server was built from, which it logs at startup. CI passes it in.
ARG BUILD_COMMIT=local
ENV RAIDZERO_OUTBOX_DIR=/data/outbox \
    RAIDZERO_BUILD=$BUILD_COMMIT
VOLUME /data
EXPOSE 7770/udp

# Fish-Net starts the server on its own in a dedicated server build. "-logFile -" sends Unity's log to stdout,
# so docker logs shows it.
ENTRYPOINT ["./RaidZeroServer.x86_64", "-batchmode", "-nographics", "-logFile", "-"]
