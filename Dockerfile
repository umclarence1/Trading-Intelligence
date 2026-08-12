# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["TradingAutomationHub.csproj", "./"]
RUN dotnet restore "./TradingAutomationHub.csproj"

COPY . .
RUN dotnet publish "./TradingAutomationHub.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "TradingAutomationHub.dll"]
