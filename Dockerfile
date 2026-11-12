FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AlibabaCloud.Ops.Mcp.Server/AlibabaCloud.Ops.Mcp.Server.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "AlibabaCloud.Ops.Mcp.Server.dll", "--transport", "streamable-http", "--host", "0.0.0.0", "--port", "8000"]
