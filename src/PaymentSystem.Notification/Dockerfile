# Build context repo'nun kök klasörüdür (bkz. docker-compose.yml), diğer servislerle aynı.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/PaymentSystem.Notification/PaymentSystem.Notification.csproj src/PaymentSystem.Notification/
RUN dotnet restore src/PaymentSystem.Notification/PaymentSystem.Notification.csproj
COPY src/PaymentSystem.Notification/ src/PaymentSystem.Notification/
RUN dotnet publish src/PaymentSystem.Notification/PaymentSystem.Notification.csproj -c Release -o /app --no-restore

# HTTP endpoint'i yok: sadece Kafka'yı dinleyen bir worker.
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "PaymentSystem.Notification.dll"]
