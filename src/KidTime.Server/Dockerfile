FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KidTime.slnx Directory.Build.props ./
COPY src/KidTime.Domain/KidTime.Domain.csproj src/KidTime.Domain/
COPY src/KidTime.Server/KidTime.Server.csproj src/KidTime.Server/
RUN dotnet restore src/KidTime.Server/KidTime.Server.csproj
COPY src/KidTime.Domain src/KidTime.Domain
COPY src/KidTime.Server src/KidTime.Server
RUN dotnet publish src/KidTime.Server/KidTime.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /home/app/.aspnet
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "KidTime.Server.dll"]
