# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore first (better layer caching)
COPY ["FlightReadinessEngine.Api.csproj", "./"]
RUN dotnet restore "FlightReadinessEngine.Api.csproj"

# Copy the rest of the source and publish
COPY . .
RUN dotnet publish "FlightReadinessEngine.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Cloud Run injects the PORT env var (defaults to 8080). ASP.NET must listen on it.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FlightReadinessEngine.Api.dll"]
