# Pin base images to digests in production.
# Refresh with: docker pull <image> && docker inspect --format='{{index .RepoDigests 0}}' <image>
# Stage 1 — build Login SPA
FROM node:20-alpine AS login-build
WORKDIR /app
COPY frontend/login/package*.json ./
RUN npm ci
COPY frontend/login/ ./
RUN npm run build

# Stage 2 — build Admin SPA
FROM node:20-alpine AS admin-build
WORKDIR /app
COPY frontend/admin/package*.json ./
RUN npm ci
COPY frontend/admin/ ./
RUN npm run build

# Stage 3 — build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY src/ ./
# Copy SPA dist into wwwroot before publish
COPY --from=login-build /app/dist /src/wwwroot/
COPY --from=admin-build /app/dist /src/wwwroot/admin/
RUN dotnet publish RediensIAM.csproj -c Release -o /publish

# Stage 4 — runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend-build /publish ./
ENV ASPNETCORE_URLS=http://+:5000;http://+:5001
EXPOSE 5000 5001
USER app
ENTRYPOINT ["dotnet", "RediensIAM.dll"]
