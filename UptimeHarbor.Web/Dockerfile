# См. статью по ссылке https://aka.ms/customizecontainer, чтобы узнать как настроить контейнер отладки и как Visual Studio использует этот Dockerfile для создания образов для ускорения отладки.

# Этот этап используется при запуске из VS в быстром режиме (по умолчанию для конфигурации отладки)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
USER root
RUN mkdir -p /app/Logs \
    && chown $APP_UID /app/Logs \
    && chmod 700 /app/Logs
VOLUME ["/app/Logs"]
USER $APP_UID
EXPOSE 8080


# Этот этап используется для сборки проекта службы
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends nodejs npm \
    && rm -rf /var/lib/apt/lists/*
COPY ["UptimeHarbor.Domain/UptimeHarbor.Domain.csproj", "UptimeHarbor.Domain/"]
COPY ["UptimeHarbor.Application/UptimeHarbor.Application.csproj", "UptimeHarbor.Application/"]
COPY ["UptimeHarbor.Infrastructure/UptimeHarbor.Infrastructure.csproj", "UptimeHarbor.Infrastructure/"]
COPY ["UptimeHarbor.Web/UptimeHarbor.Web.csproj", "UptimeHarbor.Web/"]
COPY ["libs/EFCoreLibrary.dll", "libs/"]
COPY ["libs/HttpClientLibrary.dll", "libs/"]
RUN dotnet restore "./UptimeHarbor.Web/UptimeHarbor.Web.csproj"
COPY . .
WORKDIR "/src/UptimeHarbor.Web"
RUN dotnet build "./UptimeHarbor.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Этот этап используется для публикации проекта службы, который будет скопирован на последний этап
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./UptimeHarbor.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Этот этап используется в рабочей среде или при запуске из VS в обычном режиме (по умолчанию, когда конфигурация отладки не используется)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "UptimeHarbor.Web.dll"]
