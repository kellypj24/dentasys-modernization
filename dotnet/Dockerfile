# One build, two images:
#   docker build --target api    -t dentasys-api .
#   docker build --target worker -t dentasys-worker .
#
# The SDK stage runs on the build machine's own architecture and cross-publishes
# for the target, so an amd64 image builds on Apple Silicon without emulating the
# compiler. Azure Container Apps runs amd64 only.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet publish src/Dentasys.Api    -a $TARGETARCH -c Release -o /out/api \
 && dotnet publish src/Dentasys.Worker -a $TARGETARCH -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dentasys.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "dentasys-worker.dll"]
