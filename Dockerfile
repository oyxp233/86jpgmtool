# 阶段1：构建
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# 复制项目文件并还原依赖
COPY DfoGmTool.csproj .
RUN dotnet restore

# 复制所有源代码并发布（Linux 自包含）
COPY . .
RUN dotnet publish DfoGmTool.csproj -c Release -r linux-x64 --self-contained true -o /app/publish

# 阶段2：运行时
FROM debian:bookworm-slim
WORKDIR /app

# 安装运行时依赖（SQLite 等需要）
RUN apt-get update && apt-get install -y \
    libicu72 \
    libssl3 \
    && rm -rf /var/lib/apt/lists/*

# 从构建阶段复制发布结果
COPY --from=build /app/publish .

# 暴露 Web 端口（README 中为 5050）
EXPOSE 5050

# 启动命令（需要挂载服务端数据目录，通过 --server-bin 指定）
ENTRYPOINT ["./DfoGmTool"]
CMD ["--server-bin", "/data"]
