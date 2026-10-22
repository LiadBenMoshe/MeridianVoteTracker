# Build context: repository root (see docker-compose.yml)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Meridian.sln ./
COPY src/Meridian.Core/Meridian.Core.csproj src/Meridian.Core/
COPY src/Meridian.Infrastructure/Meridian.Infrastructure.csproj src/Meridian.Infrastructure/
COPY src/Meridian.Api/Meridian.Api.csproj src/Meridian.Api/
RUN dotnet restore src/Meridian.Api/Meridian.Api.csproj
COPY src/ src/
# The OpenAPI file is generated on developer builds; not needed inside the image.
RUN dotnet publish src/Meridian.Api/Meridian.Api.csproj -c Release -o /app /p:OpenApiGenerateDocumentsOnBuild=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Meridian.Api.dll"]
