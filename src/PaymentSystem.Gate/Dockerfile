# Build context repo'nun kök klasörüdür (bkz. docker-compose.yml), çünkü Gate PaymentSystem.Iso8583'ü de kullanır.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/PaymentSystem.Iso8583/PaymentSystem.Iso8583.csproj src/PaymentSystem.Iso8583/
COPY src/PaymentSystem.Gate/PaymentSystem.Gate.csproj src/PaymentSystem.Gate/
RUN dotnet restore src/PaymentSystem.Gate/PaymentSystem.Gate.csproj
COPY src/PaymentSystem.Iso8583/ src/PaymentSystem.Iso8583/
COPY src/PaymentSystem.Gate/ src/PaymentSystem.Gate/
RUN dotnet publish src/PaymentSystem.Gate/PaymentSystem.Gate.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8583
ENTRYPOINT ["dotnet", "PaymentSystem.Gate.dll"]
