# Tests run on the builder architecture; publish produces the requested target.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore tests/ArubaMail.Tests --locked-mode \
    && dotnet test tests/ArubaMail.Tests -c Release --no-restore
ARG TARGETARCH
RUN case "$TARGETARCH" in amd64) arch=x64 ;; arm64) arch=arm64 ;; *) exit 1 ;; esac \
    && dotnet publish src/ArubaMail -c Release -a "$arch" --self-contained false -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ARUBA_MAIL_ROOT=/app \
    ARUBA_MAIL_DB_PATH=/data/followups.sqlite3 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp
RUN mkdir /data && chown 10001:10001 /data && chmod 700 /data
COPY --from=build /publish/ ./
COPY accounts.example.json ./
USER 10001:10001
ENTRYPOINT ["dotnet", "ArubaMail.dll"]
