FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore layers
COPY src/AgentMemory/AgentMemory.csproj src/AgentMemory/
COPY src/AgentMemory.Cli/AgentMemory.Cli.csproj src/AgentMemory.Cli/
RUN dotnet restore src/AgentMemory/AgentMemory.csproj
RUN dotnet restore src/AgentMemory.Cli/AgentMemory.Cli.csproj

# Copy everything and build
COPY . .

RUN dotnet publish src/AgentMemory/AgentMemory.csproj -c Release -o /app/publish --no-restore
RUN dotnet publish src/AgentMemory.Cli/AgentMemory.Cli.csproj -c Release -o /app/cli --no-restore

# === Runtime image ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Copy the server build (entrypoint) and the admin CLI (run via `docker compose exec`,
# e.g. `docker compose exec server dotnet /app/cli/AgentMemory.Cli.dll list-memories --server http://localhost:8080`
# — the server listens on 8080 inside the container; the CLI's own default of 5098 is for
# the local `dotnet run` dev workflow, see src/AgentMemory/Properties/launchSettings.json)
COPY --from=build /app/publish .
COPY --from=build /app/cli ./cli

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "AgentMemory.dll"]
