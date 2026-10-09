> 正文一律用中文填写。标题使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` …）。

## 概述

说明要解决的问题和本次改动。

Closes #

## 范围

引用所链接 issue 的“范围”，并逐项交代：

- **范围内，已完成：**

## 接口、行为与兼容性

- **本次改动保证哪些行为，哪些行为不在保证范围内：** 是否与 issue 一致；不适用写“无”。
- **HTTP API、JSON、JWT 与授权：**
- **PostgreSQL schema、migration 与数据：**
- **对象存储、配置与 Consul 键：**
- **管理端、容器与部署：**
- **英文文档：**

没有影响的项目写“无”。

## 验证

列出实际执行的命令、结果和跳过原因：

- [ ] `dotnet build src/Doctheca.sln --configuration Release` 通过
- [ ] `dotnet test src/Doctheca.sln --configuration Release --no-build` 通过
- [ ] `cd frontend && npm ci && npm run build` 通过
- [ ] 涉及 schema 时，migration 已生成并审阅
- [ ] 涉及容器或部署时，镜像构建与启动验证通过
- [ ] 行为、配置或用法变化时，英文文档已同步
- [ ] 不包含密钥、连接字符串、凭据、token 或私有数据
