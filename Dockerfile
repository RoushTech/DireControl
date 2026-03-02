# Stage 1: Build Vue frontend
FROM node:22-alpine AS frontend-build
WORKDIR /src/DireControl.Vue

COPY DireControl.Vue/package.json DireControl.Vue/package-lock.json ./
RUN npm ci

COPY DireControl.Vue/ ./
RUN npm run build

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
ARG version=0.0.0
ARG gitsha=unknown
WORKDIR /src

COPY DireControl/DireControl.csproj DireControl/
COPY DireControl.Api/DireControl.Api.csproj DireControl.Api/
RUN dotnet restore DireControl.Api/DireControl.Api.csproj

COPY DireControl/ DireControl/
COPY DireControl.Api/ DireControl.Api/
RUN shortsha=$(printf '%.8s' "$gitsha") \
    && echo "Building version $version+$shortsha from $gitsha" \
    && dotnet publish DireControl.Api/DireControl.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:Version=$version \
        /p:SourceRevisionId=$shortsha \
        /p:InformationalVersion=$version+$shortsha

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=backend-build /app/publish .
COPY --from=frontend-build /src/DireControl.Vue/dist ./wwwroot

# SQLite database lives in /data so it can be mounted as a volume
ENV ConnectionStrings__Default="Data Source=/data/direcontrol.db"
ENV ASPNETCORE_URLS="http://+:5010"
ENV ASPNETCORE_ENVIRONMENT="Production"

EXPOSE 5010

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "DireControl.Api.dll"]
