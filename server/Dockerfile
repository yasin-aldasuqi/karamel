# Karamel's server as a container image. Built by Fly.io on deploy, and in CI.

# Build stage: the full .NET SDK, used only to compile.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
# Restore from the package lists alone first, so this layer stays cached until a package
# changes. --locked-mode fails if packages.lock.json is out of date.
COPY global.json Directory.Build.props ./
COPY Karamel.Api/Karamel.Api.csproj Karamel.Api/packages.lock.json Karamel.Api/
RUN dotnet restore Karamel.Api/Karamel.Api.csproj --locked-mode
COPY Karamel.Api/ Karamel.Api/
RUN dotnet publish Karamel.Api/Karamel.Api.csproj --configuration Release --no-restore --output /app

# Run stage: Microsoft's "chiseled" ASP.NET Core runtime. No shell, no package manager, and a
# non-root user by default: much less for an attacker to use. The 10.0 tag picks up .NET
# security patches on every rebuild.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# Kestrel listens on 8080 in Microsoft's images (ASPNETCORE_HTTP_PORTS).
EXPOSE 8080
# Arguments go after this: "migrate" (Fly's release command) or "generate-vapid-keys".
ENTRYPOINT ["dotnet", "Karamel.Api.dll"]
