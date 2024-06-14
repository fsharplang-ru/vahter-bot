FROM mcr.microsoft.com/dotnet/sdk:7.0.402-jammy as build-env

### workaround for testcontainers resource reaper issue
ARG RESOURCE_REAPER_SESSION_ID="00000000-0000-0000-0000-000000000000"
LABEL "org.testcontainers.resource-reaper-session"=$RESOURCE_REAPER_SESSION_ID
### end of workaround

WORKDIR /src/VahterBanBot
COPY src/VahterBanBot/VahterBanBot.fsproj .
RUN dotnet restore
COPY src/VahterBanBot .
COPY global.json .
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:7.0 as runtime
WORKDIR /publish
COPY --from=build-env /publish .
ENTRYPOINT ["dotnet", "VahterBanBot.dll"]
