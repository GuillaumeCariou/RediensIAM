# Pin base images to digests in production.
# Refresh with: docker pull <image> && docker inspect --format='{{index .RepoDigests 0}}' <image>
# Stage 1 — build Login SPA
FROM node:20-alpine AS login-build
WORKDIR /src/frontend/login
COPY frontend/login/package*.json ./
RUN npm ci
COPY frontend/login/ ./
RUN npm run build

# Stage 2 — build Admin SPA
#
# The working directory mirrors the repository layout on purpose. The console imports the browser
# SDK from source through an alias in vite.config.ts and tsconfig.app.json that spells the path
# relatively — `../../sdk/typescript/rediensiam-web/src/index.ts` — so that tree has to exist in
# this stage AND sit at exactly that offset from the app. Building in a bare /app pointed the alias
# outside the stage, and because deploy.sh builds the SPAs on the host first, a failed image build
# still had a dist to ship: the deploy reported success and pushed a stale image.
FROM node:20-alpine AS admin-build
WORKDIR /src/frontend/admin
COPY frontend/admin/package*.json ./
RUN npm ci
COPY sdk/typescript/rediensiam-web /src/sdk/typescript/rediensiam-web
COPY frontend/admin/ ./
RUN npm run build

# Stage 3 — build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY src/ ./
# Copy SPA dist into wwwroot before publish
COPY --from=login-build /src/frontend/login/dist /src/wwwroot/
COPY --from=admin-build /src/frontend/admin/dist /src/wwwroot/console/
RUN dotnet publish RediensIAM.csproj -c Release -o /publish

# Stage 4 — runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend-build /publish ./
ENV ASPNETCORE_URLS=http://+:5000;http://+:5001
EXPOSE 5000 5001
USER app
ENTRYPOINT ["dotnet", "RediensIAM.dll"]
