FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY Erp.Core.sln ./
COPY src/Erp.Core.Domain/Erp.Core.Domain.csproj src/Erp.Core.Domain/
COPY src/Erp.Core.Infrastructure/Erp.Core.Infrastructure.csproj src/Erp.Core.Infrastructure/
COPY src/Erp.Core.Api/Erp.Core.Api.csproj src/Erp.Core.Api/

RUN dotnet restore src/Erp.Core.Api/Erp.Core.Api.csproj

COPY src/ src/
RUN dotnet publish src/Erp.Core.Api/Erp.Core.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Erp.Core.Api.dll"]

