FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY erp-sdk/ erp-sdk/
COPY erp-apps/bank.iso20022/ erp-apps/bank.iso20022/
RUN dotnet publish erp-apps/bank.iso20022/Bank.Iso20022.App.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:50051
EXPOSE 50051
ENTRYPOINT ["dotnet", "Bank.Iso20022.App.dll"]
