# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
WORKDIR /src

COPY ["global.json", "./"]
COPY ["SpaghettNET.sln", "./"]
COPY ["Bot.API/Bot.API.csproj", "Bot.API/"]
COPY ["Bot.Application/Bot.Application.csproj", "Bot.Application/"]
COPY ["Bot.Infrastructure/Bot.Infrastructure.csproj", "Bot.Infrastructure/"]
COPY ["Bot.Persistence/Bot.Persistence.csproj", "Bot.Persistence/"]
RUN dotnet restore "Bot.API/Bot.API.csproj"

COPY . .
RUN case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        arm) RID=linux-arm ;; \
        *) RID=linux-$TARGETARCH ;; \
    esac \
    && dotnet publish "Bot.API/Bot.API.csproj" \
        -c Release \
        -r "$RID" \
        --self-contained false \
        -o /app/publish \
        /p:CopyPublishOutput=false \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /app /data
USER $APP_UID

ENV DOTNET_EnableDiagnostics=0
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

ENTRYPOINT ["dotnet", "Bot.API.dll"]
