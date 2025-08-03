FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["TimeStore/TimeStore.csproj", "TimeStore/"]
COPY ["TimeStore.Core/TimeStore.Core.csproj", "TimeStore.Core/"]
RUN dotnet restore "TimeStore/TimeStore.csproj"
COPY . .
WORKDIR "/src/TimeStore"
RUN dotnet build "TimeStore.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TimeStore.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TimeStore.dll"]
