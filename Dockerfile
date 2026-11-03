# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish

ARG REPOSITORY_URL
ARG APP_VERSION
ARG NIGHTLY
ARG PUBLISH_HID=false
ARG HID_VERSION
ARG SOURCE_REVISION

WORKDIR /source

COPY Directory.Build.props g-eBAR.sln ./
COPY GeBar.Core/GeBar.Core.csproj GeBar.Core/
COPY GeBar.Hid/GeBar.Hid.csproj GeBar.Hid/
COPY GeBar.UI/GeBar.UI.csproj GeBar.UI/
COPY GeBar.App/GeBar.App.csproj GeBar.App/
COPY GeBar.HidApp/GeBar.HidApp.csproj GeBar.HidApp/
COPY GeBar.Tests/GeBar.Tests.csproj GeBar.Tests/

RUN --mount=type=cache,id=gebar-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore g-eBAR.sln --runtime win-x64 -p:ContinuousIntegrationBuild=true

COPY GeBar.Core/ GeBar.Core/
COPY GeBar.Hid/ GeBar.Hid/
COPY GeBar.UI/ GeBar.UI/
COPY GeBar.App/ GeBar.App/
COPY GeBar.HidApp/ GeBar.HidApp/
COPY GeBar.Tests/ GeBar.Tests/

RUN --mount=type=cache,id=gebar-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet build GeBar.Tests/GeBar.Tests.csproj \
    --configuration Release \
    --runtime win-x64 \
    --no-restore \
    --nologo \
    -p:ContinuousIntegrationBuild=true

RUN --mount=type=cache,id=gebar-nuget,target=/root/.nuget/packages,sharing=locked \
    VERSION="${APP_VERSION#v}" && VERSION_PROPERTY="" && if [ -n "$VERSION" ]; then VERSION_PROPERTY="-p:Version=$VERSION"; fi && dotnet publish GeBar.App/GeBar.App.csproj \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    --no-restore \
    --output /out \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryUrl=$REPOSITORY_URL \
    -p:SourceRevisionId=$SOURCE_REVISION \
    $VERSION_PROPERTY \
    -p:Nightly=$NIGHTLY

RUN --mount=type=cache,id=gebar-nuget,target=/root/.nuget/packages,sharing=locked \
    if [ "$PUBLISH_HID" = "true" ]; then dotnet publish GeBar.HidApp/GeBar.HidApp.csproj \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    --no-restore \
    --output /probe \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:ContinuousIntegrationBuild=true \
    -p:RepositoryUrl=$REPOSITORY_URL \
    -p:Version=1.0.0 \
    -p:Nightly=$NIGHTLY \
    -p:InformationalVersion=$HID_VERSION; fi

RUN if [ "$PUBLISH_HID" = "true" ]; then cp "/probe/g-eHID.exe" "/out/g-eHID.exe"; fi

FROM scratch AS artifact
COPY --from=publish /out/ /
COPY LICENSE /LICENSE
