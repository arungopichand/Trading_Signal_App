FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["SignalFeed.Api.csproj", "./"]
RUN dotnet restore "SignalFeed.Api.csproj"

COPY . .
RUN dotnet publish "SignalFeed.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render provides PORT at runtime; default to 10000 if not present.
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "SignalFeed.Api.dll"]
