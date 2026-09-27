FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore layers
COPY src/AgentMemory/AgentMemory.csproj src/AgentMemory/
RUN dotnet restore src/AgentMemory/AgentMemory.csproj

# Copy everything and build
COPY . .
WORKDIR /src/src/AgentMemory
RUN dotnet publish -c Release -o /app/publish

# === Runtime image ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Copy the server build
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "AgentMemory.dll"]