FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/MisAIPhoneAttendant/MisAIPhoneAttendant.csproj src/MisAIPhoneAttendant/
RUN dotnet restore src/MisAIPhoneAttendant/MisAIPhoneAttendant.csproj
COPY . .
RUN dotnet publish src/MisAIPhoneAttendant/MisAIPhoneAttendant.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MisAIPhoneAttendant.dll"]
