# Banter.Server container.
#
# The admin password is configuration, not a build input: baking it into an image would put a
# credential in every layer and every registry that image reaches. Set BANTER_ADMIN_PASSWORD at
# run time (see compose.yaml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project graph first so a source-only change does not re-download packages.
COPY Directory.Build.props NuGet.config ./
COPY src/ ./src/

# The server pulls MCPHub.Proxy from the Wixely feed, and NuGet.config expects credentials to be
# injected by name rather than stored in the file. A developer's machine has them in the user-level
# config; a container has nothing, so they are passed in.
#
# The user is a plain build-arg and the token is a BuildKit secret, so the token is never a layer
# and never reaches `docker history`. --store-password-in-clear-text writes it into this stage's
# NuGet.config, which is why the runtime stage below copies only /app: the build stage, and the
# credential in it, is discarded.
#
# Both optional. Left unset the restore is anonymous, which is enough if every Wixely package the
# server needs is public — but a private one then fails with NU1301 rather than silently building
# something different.
ARG GITHUB_PACKAGES_USER=""
RUN --mount=type=secret,id=github_packages_token \
    if [ -n "${GITHUB_PACKAGES_USER}" ] && [ -s /run/secrets/github_packages_token ]; then \
      dotnet nuget update source GitHub-Wixely-Packages \
        --username "${GITHUB_PACKAGES_USER}" \
        --password "$(cat /run/secrets/github_packages_token)" \
        --store-password-in-clear-text --configfile NuGet.config; \
    fi; \
    dotnet restore src/Banter.Server/Banter.Server.csproj

RUN dotnet publish src/Banter.Server/Banter.Server.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Rooms, messages, tasks and uploaded files live here. Mount it, or a restart loses the lot.
VOLUME ["/data"]
ENV BANTER_DB=sqlite \
    BANTER_CONNECTION="Data Source=/data/banter.db" \
    BANTER_DATA=/data/files

EXPOSE 7770

# Not root: the server only ever needs to read its own binaries and write /data.
RUN useradd --system --uid 10001 banter && mkdir -p /data && chown banter /data
USER banter

ENTRYPOINT ["dotnet", "Banter.Server.dll"]
CMD ["--endpoint", "tcp://0.0.0.0:7770"]
