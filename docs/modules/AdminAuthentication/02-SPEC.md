# 02-SPEC — DocLibrary 管理员认证规格

## HTTP 契约

### POST `/admin/auth/login`

匿名端点。请求：

```json
{
  "username": "string",
  "password": "string"
}
```

成功返回 200，并设置 Access/Refresh HttpOnly Cookie：

```json
{
  "success": true,
  "data": {
    "username": "string",
    "roles": ["admin"],
    "expiresAt": 1719900000
  }
}
```

失败语义：

| 场景 | HTTP | 响应消息 |
|------|------|----------|
| 用户名或密码为空 | 400 | `Username and password are required.` |
| Identity 拒绝账号密码 | 401 | `Invalid username or password.` |
| Identity 登录成功但 Token 无 `role=admin` | 403 | `Administrator access is required.` |
| Identity 不可用或响应无效 | 502 | `Identity service is unavailable.` |
| Identity Token 未通过密码学验证 | 502 | `Identity service returned an invalid token.` |

DocLibrary 调用 Identity 的请求体固定为：

```json
{
  "grantType": "password",
  "username": "<trimmed username>",
  "password": "<password>"
}
```

### POST `/admin/auth/refresh`

匿名端点，但要求请求携带有效 Refresh Cookie。DocLibrary 调用 Identity：

```json
{
  "grantType": "refresh_token",
  "refreshToken": "<HttpOnly cookie value>"
}
```

刷新成功后必须重新验证新 Access Token 的签名和 `role=admin`，然后轮换两个 Cookie。无效、过期、已撤销或非管理员 Token 返回 401；Identity 不可用返回 502；两类失败都清除两个 Cookie。

### POST `/admin/auth/logout`

允许无有效 Access Token 时调用，以保证过期会话仍能退出。若存在 Refresh Cookie，DocLibrary best-effort 调用 Identity `POST /api/auth/revoke`；无论 Identity 是否可用，最终均清除两个 Cookie并返回 200。

### GET `/admin/auth/session`

要求管理员策略。返回当前 JWT 中的用户 ID、名称、角色和过期时间，不返回 Token：

```json
{
  "success": true,
  "data": {
    "userId": "uuid",
    "username": "string",
    "roles": ["admin"],
    "expiresAt": 1719900000
  }
}
```

## 授权矩阵

| 调用者 | 认证端点 | 其他 `/admin/*` |
|--------|----------|-----------------|
| 未登录浏览器 | login/logout 允许；session 401 | 401 |
| 普通 Identity JWT | login 403；session 403 | 403 |
| `role=admin` Identity JWT/Cookie | 允许 | 允许 |

## JWT 验证

| 项目 | 值/来源 |
|------|---------|
| Authority | `IdentityService:Authority` |
| Issuer | `IdentityService:Issuer`，必须显式配置；旧 Issuer 仅放在 `AdditionalValidIssuers` |
| Audience | `IdentityService:Audience`，必须显式配置 |
| 签名 | Authority OIDC discovery + JWKS，RS256 |
| 有效期 | 必须校验；ClockSkew 来自 `IdentityService:ClockSkewSeconds` |
| 管理员角色 | `role=admin`，大小写不敏感 |

登录和刷新阶段的显式 Token 校验与请求管线中的 JwtBearer 校验必须使用同一组参数。

## Cookie

| Cookie | Path | 属性 | 用途 |
|--------|------|------|------|
| `doclibraryAccessToken` | `/admin` | HttpOnly, SameSite=Strict, Secure 由配置控制 | 管理 API 身份 |
| `doclibraryRefreshToken` | `/admin/auth` | HttpOnly, SameSite=Strict, Secure 由配置控制 | 刷新与登出 |

生产 HTTPS 部署必须设置 `Authentication:CookieSecure=true`。当前纯 HTTP 内网部署可显式设置为 `false`，但不得把该配置解释为公网安全部署。

## 前端行为

1. 应用启动调用 `/admin/auth/session`。
2. 200 时加载管理界面；401/403 时显示登录页。
3. 登录成功后重新请求 session，不读取 Token。
4. 共享 Axios 客户端遇到非认证端点 401 时，只调用一次 refresh，并发请求共享同一 refresh Promise。
5. 刷新成功后重试原请求；刷新失败时清理前端会话状态并显示登录页。
6. 403 不自动重试，显示无权限提示。
7. 登出完成后立即显示登录页。

## 安全约束

- 不在前端硬编码管理员用户名或密码。
- 不把密码、Token 或 Cookie 写入日志、异常消息或 API 响应。
- 登录失败日志只记录通用原因和经过规范化的非敏感关联信息。
- 管理 API 不启用跨域凭据；前端与后端保持同源。
- 静态文件和 SPA fallback 必须映射在授权策略之外。
