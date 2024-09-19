FROM mcr.microsoft.com/dotnet/sdk:8.0.302-jammy AS build-env

### workaround for testcontainers resource reaper issue
ARG RESOURCE_REAPER_SESSION_ID="00000000-0000-0000-0000-000000000000"
LABEL "org.testcontainers.resource-reaper-session"=$RESOURCE_REAPER_SESSION_ID
### end of workaround

WORKDIR /src/VahterBanBot
COPY src/VahterBanBot/VahterBanBot.fsproj .
COPY global.json .
COPY src/VahterBanBot/packages.lock.json .
RUN dotnet restore
COPY src/VahterBanBot .
COPY global.json .
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /publish
COPY --from=build-env /publish .
ENTRYPOINT ["dotnet", "VahterBanBot.dll"]
