FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/PhotoGallery.csproj src/
RUN dotnet restore src/PhotoGallery.csproj
COPY src/ src/
RUN dotnet publish src/PhotoGallery.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# exiftool: RAW preview extraction; fontconfig + fonts: text watermarks
RUN apt-get update \
 && apt-get install -y --no-install-recommends libimage-exiftool-perl fontconfig fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ARG GIT_SHA=dev
ARG BUILD_DATE=
ENV APP_COMMIT=$GIT_SHA \
    APP_BUILD_DATE=$BUILD_DATE \
    ASPNETCORE_URLS=http://+:8080 \
    PHOTO_LIBRARY=/photos \
    DATA_DIR=/data \
    DOTNET_gcServer=0
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=60s --timeout=5s CMD ["bash","-c","exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\r\n\r\n' >&3 && grep -q ok <&3"]
ENTRYPOINT ["dotnet", "PhotoGallery.dll"]
