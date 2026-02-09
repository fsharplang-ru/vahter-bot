FROM mcr.microsoft.com/dotnet/sdk:10.0.102-noble AS build-env

### workaround for testcontainers resource reaper issue
ARG RESOURCE_REAPER_SESSION_ID="00000000-0000-0000-0000-000000000000"
LABEL "org.testcontainers.resource-reaper-session"=$RESOURCE_REAPER_SESSION_ID
### end of workaround

WORKDIR /src/VahterBanBot
COPY src/VahterBanBot/VahterBanBot.fsproj .
COPY NuGet.Config .
RUN dotnet restore
COPY src/VahterBanBot .
COPY global.json .
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.2-noble AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /publish
COPY --from=build-env /publish .
ENTRYPOINT ["dotnet", "VahterBanBot.dll"]
