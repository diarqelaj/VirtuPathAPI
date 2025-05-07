# Base image for runtime
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

# Build image
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["VirtuPathAPI/VirtuPathAPI.csproj", "VirtuPathAPI/"]
RUN dotnet restore "VirtuPathAPI/VirtuPathAPI.csproj"
COPY . .
WORKDIR "/src/VirtuPathAPI"
RUN dotnet build "VirtuPathAPI.csproj" -c Release -o /app/build

# Publish image
FROM build AS publish
RUN dotnet publish "VirtuPathAPI.csproj" -c Release -o /app/publish

# Final image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "VirtuPathAPI.dll"]
