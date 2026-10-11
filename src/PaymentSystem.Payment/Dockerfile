# Build context repo'nun kök klasörüdür (bkz. docker-compose.yml), çünkü Payment PaymentSystem.Iso8583'ü de kullanır (PAN maskeleme).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/PaymentSystem.Iso8583/PaymentSystem.Iso8583.csproj src/PaymentSystem.Iso8583/
COPY src/PaymentSystem.Payment/PaymentSystem.Payment.csproj src/PaymentSystem.Payment/
RUN dotnet restore src/PaymentSystem.Payment/PaymentSystem.Payment.csproj
COPY src/PaymentSystem.Iso8583/ src/PaymentSystem.Iso8583/
COPY src/PaymentSystem.Payment/ src/PaymentSystem.Payment/
RUN dotnet publish src/PaymentSystem.Payment/PaymentSystem.Payment.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentSystem.Payment.dll"]
