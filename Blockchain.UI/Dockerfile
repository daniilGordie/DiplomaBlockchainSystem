FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish Blockchain.UI/Blockchain.UI.csproj -c Release -o /app/publish

FROM nginx:1.27-alpine
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
