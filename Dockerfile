# ProofFlow, as one image.
#
# One container runs the whole product: the pages, the internal API, the live console, the run
# worker, the scheduler and the retention sweeper. That is not a simplification for the Dockerfile's
# sake — it is what this application is. The run queue is in-memory, so a second copy of this image
# pointed at the same database would run a second scheduler and a second sweeper against it while
# its own run queue sat empty. Scale it up when there is a shared queue to scale it with.
#
#   docker compose up --build
#
# Three stages, because the frontend needs Node and the runtime does not: the image that ships has
# no npm, no SDK and no source in it.

# ---- the frontend --------------------------------------------------------------------------------

FROM node:22-alpine AS frontend
WORKDIR /src

# The lockfile alone first, so a change to a .vue file does not reinstall node_modules.
COPY src/ProofFlow.Web/package.json src/ProofFlow.Web/package-lock.json ./
RUN npm ci

COPY src/ProofFlow.Web/ ./

# `npm run build` type-checks before it bundles, so a broken component fails the image rather than
# shipping in it.
RUN npm run build

# ---- the application -----------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src

# Project files first, for the same reason: restore is the slow step and it only depends on these.
COPY Directory.Build.props ProofFlow.slnx ./
COPY src/ProofFlow.Domain/*.csproj        src/ProofFlow.Domain/
COPY src/ProofFlow.Contracts/*.csproj     src/ProofFlow.Contracts/
COPY src/ProofFlow.Application/*.csproj   src/ProofFlow.Application/
COPY src/ProofFlow.TestEngine/*.csproj    src/ProofFlow.TestEngine/
COPY src/ProofFlow.Infrastructure/*.csproj src/ProofFlow.Infrastructure/
COPY src/ProofFlow.Web/*.csproj           src/ProofFlow.Web/
COPY src/ProofFlow.FakeApi/*.csproj       src/ProofFlow.FakeApi/

RUN dotnet restore src/ProofFlow.Web/ProofFlow.Web.csproj

COPY src/ ./src/

# The bundle Vite produced, dropped in before publish so it is picked up as static content. The
# .NET build never shells out to npm — a build that needs two toolchains in one step is a build
# that fails in a way neither of them explains.
COPY --from=frontend /src/wwwroot/build ./src/ProofFlow.Web/wwwroot/build

RUN dotnet publish src/ProofFlow.Web/ProofFlow.Web.csproj -c Release -o /app --no-restore

# ---- what actually ships --------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl, for the health check below and nothing else. The runtime image has no HTTP client on the
# command line, and a HEALTHCHECK that cannot make a request is a container that reports healthy
# because nothing ever asked it.
RUN apt-get update \
 && apt-get install --no-install-recommends -y curl \
 && rm -rf /var/lib/apt/lists/*

# Not root. This process makes HTTP requests to addresses somebody typed into a form; it should not
# also be the user that can rewrite its own binaries.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin proofflow

COPY --from=backend --chown=proofflow:proofflow /app ./

# The two directories that have to outlive the image:
#
#   App_Data — the SQLite file, when that is the provider. On PostgreSQL it stays empty.
#   keys     — the Data Protection keyring, which signs the authentication cookie. Lose it and
#              everybody is signed out on the next deploy; copy it between installations and their
#              sessions become interchangeable.
#
# Both sit under /app because that is where the application looks by default, and a default that
# needs configuring to be correct is a default that will be got wrong once.
RUN mkdir -p /app/App_Data /app/keys && chown -R proofflow:proofflow /app/App_Data /app/keys

USER proofflow

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 \
  CMD curl --fail --silent --output /dev/null http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "/app/ProofFlow.Web.dll"]
