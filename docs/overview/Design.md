# Design — 服务级架构

## 分层架构

```text
Host
  Program.cs: DI / Kestrel / Authentication / Authorization / static SPA
  Identity authentication and administrator authorization
        │
Service
  AdminAuthEndpoints / IdentityAuthenticationService / IdentityTokenValidator
  Document*Endpoints
  StructaDoc client+worker / OpenSearch / analysis
        │
Domain
  document and parse services, repositories, models
        │
Database
  EF Core repositories + SQL DatabaseInitializer
```

技术栈：.NET 10、ASP.NET Core Minimal APIs、EF Core 10/Npgsql、Mapster、OpenSearch.Net、Vue 3.5/TypeScript/Vite/Element Plus。

## 访问控制架构

### 浏览器管理员

- Identity password grant 验证用户名和密码。
- Identity bootstrap 管理员在密码登录时自动获得 `role=admin`。
- Doctheca 在登录和刷新时使用 Identity OIDC/JWKS 完整验证 Access Token。
- Access/Refresh Token 仅存放在 HttpOnly、SameSite=Strict Cookie。
- `DocthecaAdmin` 策略保护管理 route group，要求有效 JWT 和 `role=admin`。
- 静态文件和 SPA fallback 保持匿名，避免登录死锁。

## 关键决策

| 决策 | 理由 |
|------|------|
| 恢复应用层认证并强制 admin 角色 | 内网隔离不能阻止任何可达调用者读写管理数据 |
| HttpOnly Cookie 而非 localStorage | 前端 JavaScript 不接触 Token，降低 XSS Token窃取风险 |
| Refresh Token轮换与登出撤销 | 保持会话体验并在退出后关闭刷新能力 |
| 登录阶段再次验证 JWT | 不只信任下游 JSON roles，确保签名和标准 Claim有效 |
| 静态 SPA 匿名 | 未登录用户必须先加载登录页 |
| 不预留未使用的 Quaestura 接口 | 当前没有调用者，避免维护无消费方的接口、凭据和配置 |
| 不自动删除遗留 import 表 | 数据删除必须由单独、显式、可审查的运维变更执行 |
| 保持单端口单镜像 | 延续自包含前端、静态托管和现有 Docker 部署 |

## 关键文档

- [管理员认证规格](../modules/AdminAuthentication/02-SPEC.md)
- [部署说明](../development/Deployment.md)
- [验证说明](../development/verification.md)
