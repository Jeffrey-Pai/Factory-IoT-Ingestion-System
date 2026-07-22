# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY FactoryIoT.slnx ./
COPY src/FactoryIoT.Domain/FactoryIoT.Domain.csproj             src/FactoryIoT.Domain/
COPY src/FactoryIoT.Application/FactoryIoT.Application.csproj   src/FactoryIoT.Application/
COPY src/FactoryIoT.Simulator/FactoryIoT.Simulator.csproj       src/FactoryIoT.Simulator/
RUN dotnet restore src/FactoryIoT.Simulator/FactoryIoT.Simulator.csproj

COPY . .
RUN dotnet publish src/FactoryIoT.Simulator/FactoryIoT.Simulator.csproj \
    -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FactoryIoT.Simulator.dll"]
