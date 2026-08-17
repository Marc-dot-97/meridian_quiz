FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Meridian.Api.csproj", "./"]
RUN dotnet restore "Meridian.Api.csproj"
COPY . . 
RUN dotnet publish "Meridian.Api.csproj" -c Release -o /app/publish/p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Meridian.Api.dll"]

