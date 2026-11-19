# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SimpleFinance.sln ./
COPY src/SimpleFinance.Domain/SimpleFinance.Domain.csproj src/SimpleFinance.Domain/
COPY src/SimpleFinance.Shared/SimpleFinance.Shared.csproj src/SimpleFinance.Shared/
COPY src/SimpleFinance.Application/SimpleFinance.Application.csproj src/SimpleFinance.Application/
COPY src/SimpleFinance.Infrastructure/SimpleFinance.Infrastructure.csproj src/SimpleFinance.Infrastructure/
COPY src/SimpleFinance.Api/SimpleFinance.Api.csproj src/SimpleFinance.Api/
COPY tests/SimpleFinance.UnitTests/SimpleFinance.UnitTests.csproj tests/SimpleFinance.UnitTests/

RUN dotnet restore src/SimpleFinance.Api/SimpleFinance.Api.csproj

COPY . .

RUN dotnet publish src/SimpleFinance.Api/SimpleFinance.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "SimpleFinance.Api.dll"]
