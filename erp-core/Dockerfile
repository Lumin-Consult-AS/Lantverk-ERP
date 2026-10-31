# Build context is the repository root (erp-core references erp-sdk): docker build -f erp-core/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY erp-core/Directory.Build.props erp-core/
COPY erp-core/Erp.Core.sln erp-core/
COPY erp-core/src/Erp.Core.Domain/Erp.Core.Domain.csproj erp-core/src/Erp.Core.Domain/
COPY erp-core/src/Erp.Core.Infrastructure/Erp.Core.Infrastructure.csproj erp-core/src/Erp.Core.Infrastructure/
COPY erp-core/src/Erp.Core.Api/Erp.Core.Api.csproj erp-core/src/Erp.Core.Api/
COPY erp-sdk/src/Erp.Sdk/Erp.Sdk.csproj erp-sdk/src/Erp.Sdk/
COPY erp-sdk/protos/ erp-sdk/protos/

RUN dotnet restore erp-core/src/Erp.Core.Api/Erp.Core.Api.csproj

COPY erp-core/src/ erp-core/src/
COPY erp-sdk/src/ erp-sdk/src/
RUN dotnet publish erp-core/src/Erp.Core.Api/Erp.Core.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
# Built-in app packages are found by walking up from /app
COPY erp-apps/ /erp-apps/
COPY erp-samples/ /erp-samples/
ENTRYPOINT ["dotnet", "Erp.Core.Api.dll"]
