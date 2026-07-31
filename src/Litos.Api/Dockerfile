# Build from the repository root:
#   docker build -f src/Litos.Api/Dockerfile -t litos-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Litos.Agent/Litos.Agent.csproj src/Litos.Agent/
COPY src/Litos.Api/Litos.Api.csproj src/Litos.Api/
COPY src/Litos.Host/Litos.Host.csproj src/Litos.Host/
COPY src/Litos.Persistence/Litos.Persistence.csproj src/Litos.Persistence/
COPY src/Litos.Providers.Anthropic/Litos.Providers.Anthropic.csproj src/Litos.Providers.Anthropic/
COPY src/Litos.Providers.Gemini/Litos.Providers.Gemini.csproj src/Litos.Providers.Gemini/
COPY src/Litos.Providers.OpenAI/Litos.Providers.OpenAI.csproj src/Litos.Providers.OpenAI/
COPY src/Litos.Providers.OpenRouter/Litos.Providers.OpenRouter.csproj src/Litos.Providers.OpenRouter/
COPY src/Litos.Tools/Litos.Tools.csproj src/Litos.Tools/
COPY src/Litos.Tools.Mcp/Litos.Tools.Mcp.csproj src/Litos.Tools.Mcp/
RUN dotnet restore src/Litos.Api/Litos.Api.csproj

COPY src/ src/
RUN dotnet publish src/Litos.Api/Litos.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Python 3 + the full .NET SDK (not just the aspnet runtime this image is based on) are
# available in /workspace for the agent's own shell tool to use — e.g. running scripts or
# `dotnet build`/`dotnet test` against a repo it's working in — distinct from the ASP.NET
# runtime this app itself runs on. The SDK is copied from the build stage rather than
# installed separately, since it's already present there and copying avoids a second
# ~700MB download for the same bits.
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-pip curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/share/dotnet /usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"

# Node.js + npm (covers npx-launched MCP servers, the majority of the published MCP server
# ecosystem) and uv/uvx (covers uvx-launched Python MCP servers) — both needed since nearly every
# reference MCP server is started one of these two ways (ReadMe_LitosApi_Mcp.md §6). uv installs
# to a user-local dir by default; relocated into /usr/local/bin since this runs as root here and
# needs to be on PATH for every user/session, not just the installing one.
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/* \
    && curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR=/usr/local/bin sh

# §5.6 filesystem confinement: the agent's workspace (file/shell tools) and its
# persisted state (~/.litos config + session transcripts) are the only two host
# paths meant to be mounted into this container — see ReadMe_HeadlessServiceTool.md §5.6.
RUN mkdir -p /workspace /data
ENV HOME=/data
ENV LITOS_WORKSPACE=/workspace
WORKDIR /workspace

COPY --from=build /app /app

# Content/web root pinned to /app regardless of WORKDIR, so static assets (wwwroot)
# resolve correctly even though the process's working directory is /workspace (needed
# for AgentWorker's Directory.GetCurrentDirectory()-based workspace root, above).
ENV ASPNETCORE_CONTENTROOT=/app

# Loopback-only by default is the caller's responsibility (docker run -p
# 127.0.0.1:8080:8080 ...) — see ReadMe_HeadlessServiceTool.md §5.5's exposure footgun.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "/app/Litos.Api.dll"]
