# Frontend build stage
FROM node:22-alpine AS web
WORKDIR /web
# Copy manifests first so the dependency layer caches independently of source churn.
COPY src/Arbitarr.Web/package.json src/Arbitarr.Web/package-lock.json ./
RUN npm ci
COPY src/Arbitarr.Web/ ./
# vite.config.ts sets outDir to ../Arbitarr.Host/wwwroot; inside this stage that
# resolves to /Arbitarr.Host/wwwroot, so the output path is pinned explicitly here.
#
# It invokes `vite build` DIRECTLY, not `npm run build`, because the `build` script
# is `tsc --noEmit && vite build` and the typecheck half is redundant here: it
# already ran as its own CI step (AC16), so repeating it inside the image build
# costs time and gates nothing new. Skipping it also keeps a type error from
# surfacing first as an opaque image-build failure.
#
# (For the record, `npm run build -- --outDir /web/dist` does WORK -- npm appends
# forwarded args to the last command in the && chain, so they reach `vite build`,
# not `tsc`. Verified by running it. It is simply the slower way to get here, so
# the reason to call vite directly is the redundant typecheck, nothing subtler.)
RUN npx vite build --outDir /web/dist --emptyOutDir

# --- Two outDirs, and which one is authoritative ---
# The build output path is written in two places with two different values:
#   vite.config.ts  build.outDir = '../Arbitarr.Host/wwwroot'   (local `npm run build`)
#   this Dockerfile --outDir /web/dist                          (container build)
# That is deliberate. The resolution is stated here so nobody "fixes" the
# divergence by making one match the other:
#
#   * /web/dist IS AUTHORITATIVE. It is what ships. The build stage below does
#     `rm -rf src/Arbitarr.Host/wwwroot` and copies /web/dist over it, so image
#     content cannot depend on a developer's local state -- the whole reason the
#     override exists.
#   * The vite.config.ts value is a DEVELOPER CONVENIENCE ONLY. It lets
#     `npm run build` + `dotnet run` serve the SPA from the host without extra
#     flags. Its output is NEVER consumed by the image.
#   * Therefore the two need NOT produce identical manifests, and no acceptance
#     criterion asserts that they do. Asserting it would be a false guarantee:
#     the container pins Node 22 and installs via `npm ci` from the lockfile,
#     while a developer's host may differ in both.
#   * Do NOT delete the vite.config.ts outDir for "one source of truth" --
#     `npm run build` would then write Vite's default `dist/`, which .gitignore
#     covers but `dotnet run` does not serve, silently breaking the local loop.
#     Do NOT point the Dockerfile at it either: the point is that the image is
#     independent of that host-relative path.
# Change /web/dist only together with the COPY --from=web line below; change the
# vite.config.ts value only if the local dev loop changes.

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Drop any host-built bundle from the context and replace it with the one this
# build just produced, so image content never depends on a developer's local state.
RUN rm -rf src/Arbitarr.Host/wwwroot
COPY --from=web /web/dist/ src/Arbitarr.Host/wwwroot/
# Arbitarr.Host uses Microsoft.NET.Sdk.Web, which treats wwwroot/** as publishable
# content by default. Because the COPY above lands the bundle on disk BEFORE this
# publish runs in the same stage, the SDK carries it into /app/publish/wwwroot with
# no csproj change, and the runtime stage's COPY below then puts it at /app/wwwroot.
#
# Issue #46/R1: build identity. These three build args default to empty so a manual
# `docker build` with none supplied still succeeds -- Arbitarr.Host.csproj treats an empty
# value as unset, and BuildInfo renders "unknown (local build)" for it rather than a blank
# or stale stamp. deploy-review.yml passes CommitSha, ImageTag and BuildTimestampUtc through --
# that workflow's `docker build` step is the only one in this repo, build-test.yml has none.
ARG COMMIT_SHA=""
ARG IMAGE_TAG=""
ARG BUILD_TIMESTAMP_UTC=""
RUN dotnet publish src/Arbitarr.Host/Arbitarr.Host.csproj -c Release -o /app/publish \
    -p:CommitSha="$COMMIT_SHA" \
    -p:ImageTag="$IMAGE_TAG" \
    -p:BuildTimestampUtc="$BUILD_TIMESTAMP_UTC"

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Runtime state (SQLite cache, fetched datasets) lives under /config,
# mounted from the host (e.g. Unraid appdata).
ENV ARBITARR_CONFIG_DIR=/config \
    ASPNETCORE_URLS=http://+:8080
VOLUME /config
EXPOSE 8080

ENTRYPOINT ["dotnet", "Arbitarr.Host.dll"]
