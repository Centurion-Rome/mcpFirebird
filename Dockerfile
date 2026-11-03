FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY McpFirebird/McpFirebird.csproj McpFirebird/
RUN dotnet restore McpFirebird/McpFirebird.csproj
COPY McpFirebird/ McpFirebird/
RUN dotnet publish McpFirebird/McpFirebird.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "McpFirebird.dll"]
