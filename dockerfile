# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy all project files
COPY . .
RUN dotnet restore "TupleServer/TupleServer.csproj"
RUN dotnet publish "TupleServer/TupleServer.csproj" -c Release -o /app/publish

# Runtime Stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose the TCP port
EXPOSE 8080

ENTRYPOINT ["dotnet", "TupleServer.dll"]