FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
# Run as the unprivileged user shipped with the .NET 8 images.
USER app
# The app listens on $PORT (default 8080, see .env.example).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Magi.Api.dll"]
