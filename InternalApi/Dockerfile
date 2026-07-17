FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["InternalApi/InternalApi.csproj", "InternalApi/"]
COPY ["ArchiveBot/ArchiveBot.csproj", "ArchiveBot/"]
COPY ["ImageFetch/ImageFetch.csproj", "ImageFetch/"]
COPY ["Common/Common.csproj", "Common/"]
RUN dotnet restore "InternalApi/InternalApi.csproj"
COPY . .
WORKDIR "/src/InternalApi"
RUN dotnet build "./InternalApi.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./InternalApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "InternalApi.dll"]
