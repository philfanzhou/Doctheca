# Verification Guide

## 前置条件

- DocLibrary：`http://localhost:5012`
- Identity：`http://localhost:5002`
- Identity 已通过 `AdminBootstrap` 植入管理员
- 管理员密码来自 Identity 部署环境的 `ADMIN_BOOTSTRAP_PASSWORD`
- DocLibrary 已配置 `IdentityService:Authority`

不得把真实密码、Token 或 Cookie 提交到仓库或粘贴到测试报告。

## 匿名入口

```bash
curl -i http://localhost:5012/
curl -i http://localhost:5012/health
```

预期均非 401。健康检查返回 200。

## 未登录管理 API

```bash
curl -i http://localhost:5012/admin/document-files
```

预期：401。

## 管理员登录

```bash
curl -i -X POST http://localhost:5012/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<ADMIN_BOOTSTRAP_PASSWORD>"}' \
  -c doclibrary.cookies
```

预期：200；响应体不含 `accessToken` 或 `refreshToken`；`Set-Cookie` 包含 HttpOnly 的 `doclibraryAccessToken` 和 `doclibraryRefreshToken`。

用户名来自 Identity `AdminBootstrap:Username` 配置，不应由 DocLibrary 前端硬编码。

## 错误密码与普通账户

```bash
curl -i -X POST http://localhost:5012/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"wrong-password"}'
```

预期：401，统一错误消息，不设置认证 Cookie。

使用正确的普通 Identity 账户密码重复调用，预期：403，不设置认证 Cookie。

## 会话与管理操作

```bash
curl -i http://localhost:5012/admin/auth/session -b doclibrary.cookies
curl -i http://localhost:5012/admin/document-files -b doclibrary.cookies
```

预期：200，session 的 `roles` 包含 `admin`，响应不包含 Token。

上传、解析、元数据、删除、搜索和导出请求都必须携带 Cookie。例如：

```bash
curl -i -X POST http://localhost:5012/admin/document-files/upload \
  -b doclibrary.cookies \
  -F "file=@test.pdf"
```

## 刷新

```bash
curl -i -X POST http://localhost:5012/admin/auth/refresh \
  -b doclibrary.cookies \
  -c doclibrary.cookies
```

预期：200，轮换两个 Cookie，响应体不含 Token。无效 Refresh Cookie返回 401 并清除认证 Cookie。

## 登出

```bash
curl -i -X POST http://localhost:5012/admin/auth/logout \
  -b doclibrary.cookies \
  -c doclibrary.cookies

curl -i http://localhost:5012/admin/document-files -b doclibrary.cookies
```

预期：登出返回 200；后续管理请求返回 401。

## 已移除接口

`/internal/question-bank/*`、旧 `/admin/document-parses/importable` 和
`POST .../import-status` 均不应映射。

## 自动化验证

```bash
cd src/services/ruoyu.doclibrary
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests/Ruoyu.Study.DocLibrary.Tests.csproj --configuration Release
dotnet build src/Ruoyu.Study.DocLibrary.sln --configuration Release

cd frontend
npm run build
```

完整仓库快速验证：

```bash
./tests/integration/scripts/pre-commit.sh --skip-docker --skip-e2e
```
