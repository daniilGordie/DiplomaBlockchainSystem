FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish Blockchain.UI/Blockchain.UI.csproj -c Release -o /app/publish

FROM nginx:1.27-alpine
ARG UI_NODE_URL=http://localhost:7042
ARG UI_IPFS_API_URL=http://127.0.0.1:5001/api/v0/add
ARG UI_IPFS_GATEWAY_URL=http://127.0.0.1:8080/ipfs
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
RUN sed -i "s#\"NodeUrl\": \".*\"#\"NodeUrl\": \"${UI_NODE_URL}\"#" /usr/share/nginx/html/appsettings.json \
    && sed -i "s#\"IpfsApiUrl\": \".*\"#\"IpfsApiUrl\": \"${UI_IPFS_API_URL}\"#" /usr/share/nginx/html/appsettings.json \
    && sed -i "s#\"IpfsGatewayUrl\": \".*\"#\"IpfsGatewayUrl\": \"${UI_IPFS_GATEWAY_URL}\"#" /usr/share/nginx/html/appsettings.json
