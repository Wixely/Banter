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
RUN dotnet restore src/Banter.Server/Banter.Server.csproj

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
