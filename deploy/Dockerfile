# syntax=docker/dockerfile:1
# Imagens do Api e do Worker do ia.bimeta (ADR 0033). Contexto de build: a RAIZ do repositorio (veja docker-compose.yml).
# Tag da base: .NET 10 sobre Ubuntu 24.04 (noble), a mesma familia do host. Nada e baixado alem dos pacotes do NuGet (nuget.org).
ARG DOTNET_TAG=10.0-noble

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
WORKDIR /src
COPY nuget.config Directory.Build.props ./
COPY backend ./backend
# O global.json do repositorio fixa a faixa do SDK do desenvolvedor; a imagem ja fixa a versao pela tag, entao ele nao entra no build.
RUN dotnet restore backend/host/IaBimeta.Api/IaBimeta.Api.csproj \
 && dotnet restore backend/host/IaBimeta.Worker/IaBimeta.Worker.csproj
RUN dotnet publish backend/host/IaBimeta.Api/IaBimeta.Api.csproj -c Release -o /out/api --no-restore -p:UseAppHost=false \
 && dotnet publish backend/host/IaBimeta.Worker/IaBimeta.Worker.csproj -c Release -o /out/worker --no-restore -p:UseAppHost=false

# Base de execucao: o Worker tambem usa o framework ASP.NET (Generic Host), entao as duas imagens partem da mesma base.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
# A pasta privada dos arquivos de midia: criada e entregue ao usuario do servico ANTES do volume nomeado (que herda o dono na primeira montagem).
RUN mkdir -p /var/lib/iabimeta/anexos && chown -R "${APP_UID:-1654}:${APP_UID:-1654}" /var/lib/iabimeta
USER ${APP_UID:-1654}

FROM runtime AS api
COPY --from=build /out/api ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Todos os comandos de operacao (migrate, provision, attachments, retention, status, reset-password) sao argumentos deste ponto de entrada.
ENTRYPOINT ["dotnet", "IaBimeta.Api.dll"]

FROM runtime AS worker
COPY --from=build /out/worker ./
ENTRYPOINT ["dotnet", "IaBimeta.Worker.dll"]
