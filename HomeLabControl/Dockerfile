# Контекст сборки — корень репозитория (hlc/):  docker build -f HomeLabControl/Dockerfile .
# Образ содержит HomeLabControl и готовые сборки агента для деплоя: /app/agent/linux-x64, /app/agent/win-x64.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
    wakeonlan \
    openssh-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8208

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["HomeLabControl/HomeLabControl.csproj", "HomeLabControl/"]
COPY ["HomeLabControlAgent/HomeLabControlAgent.csproj", "HomeLabControlAgent/"]
COPY ["ShutdownDialog/ShutdownDialog.csproj", "ShutdownDialog/"]
RUN dotnet restore "HomeLabControl/HomeLabControl.csproj" \
 && dotnet restore "HomeLabControlAgent/HomeLabControlAgent.csproj" -r linux-x64 \
 && dotnet restore "HomeLabControlAgent/HomeLabControlAgent.csproj" -r win-x64 -p:EnableWindowsTargeting=true \
 && dotnet restore "ShutdownDialog/ShutdownDialog.csproj" -r win-x64
COPY . .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "HomeLabControl/HomeLabControl.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Агент — self-contained: на целевых машинах не нужен установленный .NET
FROM build AS agent
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "HomeLabControlAgent/HomeLabControlAgent.csproj" -c $BUILD_CONFIGURATION -r linux-x64 --self-contained true -o /agent/linux-x64 \
 && dotnet publish "HomeLabControlAgent/HomeLabControlAgent.csproj" -c $BUILD_CONFIGURATION -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -o /agent/win-x64

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=agent /agent ./agent
ENTRYPOINT ["dotnet", "HomeLabControl.dll"]
