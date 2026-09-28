# PeakRelay server — build + run in one stage
# Build:  docker build -t peakrelay-server PeakRelay/
# Run:    docker run -p 5055:5055 -p 5056:5056 peakrelay-server
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY PeakRelay.Protocol/ PeakRelay.Protocol/
COPY PeakRelay.Server/ PeakRelay.Server/
RUN dotnet publish PeakRelay.Server/PeakRelay.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .

# 5055 = relay (Photon LB TCP), 5056 = room-list HTTP
EXPOSE 5055 5056

ENTRYPOINT ["dotnet", "PeakRelay.Server.dll", "5055", "5056"]
