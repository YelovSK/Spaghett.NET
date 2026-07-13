# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["global.json", "./"]
COPY ["SpaghettNET.sln", "./"]
COPY ["SpaghettNET.Bot/SpaghettNET.Bot.csproj", "SpaghettNET.Bot/"]
COPY ["SpaghettNET.Platform/SpaghettNET.Platform.csproj", "SpaghettNET.Platform/"]
COPY ["SpaghettNET.Persistence/SpaghettNET.Persistence.csproj", "SpaghettNET.Persistence/"]
RUN dotnet restore "SpaghettNET.Bot/SpaghettNET.Bot.csproj"

COPY . .
RUN dotnet publish "SpaghettNET.Bot/SpaghettNET.Bot.csproj" \
        -c Release \
        -r linux-x64 \
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

ENTRYPOINT ["dotnet", "SpaghettNET.Bot.dll"]
