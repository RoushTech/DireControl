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

COPY DireControl/ DireControl/
COPY DireControl.Modem/ DireControl.Modem/
COPY DireControl.Api/ DireControl.Api/
RUN shortsha=$(printf '%.8s' "$gitsha") \
    && echo "Building version $version+$shortsha from $gitsha" \
    && dotnet publish DireControl.Api/DireControl.Api.csproj \
        -c Release \
        -o /app/publish \
        /p:Version=$version \
        /p:EnableSourceControlManagerQueries=false \
        /p:SourceRevisionId=$shortsha \
        /p:IncludeSourceRevisionInInformationalVersion=false \
        /p:InformationalVersion=$version+$shortsha
        

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libasound is required by the native sound modem (ALSA capture/playback);
# libgpiod supports GPIO PTT.  The container additionally needs the relevant
# devices passed through, e.g. `devices: ["/dev/snd:/dev/snd"]` in
# docker-compose (plus /dev/ttyUSB0, /dev/hidraw0, or /dev/gpiochip0 for PTT).
# Package names differ by base-image distro (Ubuntu noble: libasound2t64 /
# libgpiod2; Debian: libasound2 / libgpiod3) — satisfy picks whichever exists.
RUN apt-get update \
    && apt-get satisfy -y --no-install-recommends \
        "libasound2t64 | libasound2" \
        "libgpiod2 | libgpiod3" \
    && rm -rf /var/lib/apt/lists/*

COPY --from=backend-build /app/publish .
COPY --from=frontend-build /src/DireControl.Vue/dist ./wwwroot

# SQLite database lives in /data so it can be mounted as a volume
ENV ConnectionStrings__Default="Data Source=/data/direcontrol.db"
ENV ASPNETCORE_URLS="http://+:5010"
ENV ASPNETCORE_ENVIRONMENT="Production"

EXPOSE 5010

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "DireControl.Api.dll"]
