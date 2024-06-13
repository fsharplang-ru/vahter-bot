FROM mcr.microsoft.com/dotnet/sdk:8.0.302-jammy as build-env
WORKDIR /src/VahterBanBot
COPY src/VahterBanBot/VahterBanBot.fsproj .
RUN dotnet restore
COPY src/VahterBanBot .
COPY global.json .
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 as runtime
WORKDIR /publish
COPY --from=build-env /publish .
ENTRYPOINT ["dotnet", "VahterBanBot.dll"]
