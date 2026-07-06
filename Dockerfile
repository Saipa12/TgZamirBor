FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NuGet.Config ./
COPY TgZamirBor.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish TgZamirBor.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /app/publish .

ENV DATA_DIR=/data
RUN mkdir -p /data
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "TgZamirBor.dll"]
