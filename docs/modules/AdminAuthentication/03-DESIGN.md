# 03-DESIGN — Doctheca 管理员认证设计

## 组件边界

```text
Browser
  ├─ anonymous SPA/login page
  └─ HttpOnly cookies
          │
          ▼
Doctheca Host
  ├─ AdminAuthEndpoints
  ├─ IdentityAuthenticationService
  ├─ IdentityTokenValidator
  ├─ JwtBearer + DocthecaAdmin policy
  └─ existing /admin domain endpoints
          │
          ▼
SignaCore
  ├─ POST /api/auth/token
  ├─ POST /api/auth/revoke
  └─ OIDC discovery + JWKS
```

### `AdminAuthEndpoints`

只负责 HTTP 契约、Cookie 写入/删除和结果映射，不直接实现下游协议或 Token 密码学验证。

### `IdentityAuthenticationService`

封装 Identity 的 password grant、refresh token grant 和 revoke 调用。请求 DTO 使用显式 camelCase JSON 名称；token 请求附带 Doctheca 独立 AppId/AppSecret。服务不记录请求密码、应用密钥或响应 Token。

### `IdentityTokenValidator`

使用 Authority 的 OIDC metadata/JWKS 和与 JwtBearer 相同的 TokenValidationParameters 验证 Identity Access Token，返回经验证的 `ClaimsPrincipal`。登录与刷新仅在 principal 包含 `role=admin` 时建立 Cookie。

### JwtBearer

从 `Authorization: Bearer` 读取 Token；若 Header 缺失，则从 `docthecaAccessToken` Cookie读取。`DocthecaAdmin` 策略要求已认证且具有 `admin` 角色。

## 中间件与端点顺序

```text
CorrelationId
DefaultFiles / StaticFiles
Authentication
Authorization
Anonymous auth endpoints
Admin route groups -> DocthecaAdmin policy
Anonymous /health
Anonymous SPA fallback
```

静态文件、`/health`、login/refresh/logout 和 SPA fallback 不受管理员策略拦截。`/admin/auth/session` 单独要求 `DocthecaAdmin`。

## 登录数据流

```text
Browser -> Doctheca: username/password
Doctheca -> Identity: POST /api/auth/token + Doctheca App credentials
Identity -> Doctheca: accessToken + refreshToken
Doctheca -> Identity discovery/JWKS: validate accessToken
Doctheca: require role=admin
Doctheca -> Browser: HttpOnly access/refresh cookies + non-sensitive session summary
```

普通账户的 Identity Token 即使签名有效，也在角色检查阶段被拒绝，且不设置任何 Cookie。

## 刷新与登出

- Refresh Token 仅发送到 `/admin/auth/refresh` 和 `/admin/auth/logout`。
- Identity 每次 refresh 都撤销旧 Refresh Token 并返回新 Token 对；Doctheca 用新 Cookie覆盖旧 Cookie。
- 刷新返回的新 Access Token 仍必须经过完整密码学验证和管理员角色检查。
- 登出时 Identity revoke 为 best-effort，本地 Cookie 清理是强制行为。

## Quaestura 边界

> Quaestura 是原 QuestionBank 服务，已于 2026-09-25 迁出为独立仓库 [philfanzhou/Quaestura](https://github.com/philfanzhou/Quaestura)（ADR-0011）。下述边界在迁出前后一致：Doctheca 从未提供其专用接口。

当前仓库没有 Quaestura → Doctheca 调用方，因此 Doctheca 不提供
Quaestura 专用 HTTP 接口、服务认证策略或服务密钥配置。后续出现真实调用
需求时重新设计数据契约与认证方式，不预留未使用接口。

原 `POST /admin/document-parses/{parseId}/import-status`、`document_parse_imports` 运行时模型和列表过滤全部移除。已有数据库中的遗留表不在启动时自动删除。

## 部署兼容

- 前端仍由 Doctheca Docker 镜像第一阶段构建并复制到 Host `wwwroot`。
- Host 仍监听单一 HTTP 端口 5012。
- Identity Authority、Audience 和 metadata HTTPS 要求复用共享 Consul
  `IdentityService` 配置，`start.sh` 不重复注入。
- Doctheca AppId/AppSecret 是部署 secret，由 `start.sh` 注入，不进入共享 Consul。
- Cookie 安全配置保留为 Doctheca 本地配置，不新增独立前端容器或反向代理。
