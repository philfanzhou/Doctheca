# 05-TESTS — DocLibrary 管理员认证测试

## 后端自动化测试

| 编号 | 场景 | 预期 |
|------|------|------|
| AUTH-UT-01 | 未登录访问管理 API | 401 |
| AUTH-UT-02 | 有效普通用户 JWT 访问管理 API | 403 |
| AUTH-UT-03 | 有效管理员 JWT 访问管理 API | 请求进入端点 |
| AUTH-UT-04 | 管理员正确账号密码登录 | 200，设置两个 HttpOnly Cookie，响应不含 Token |
| AUTH-UT-05 | 错误密码登录 | 401，统一错误消息，无 Cookie |
| AUTH-UT-06 | 普通账户正确账号密码登录 | 403，无 Cookie |
| AUTH-UT-07 | Identity 返回伪造、错误 issuer、错误 audience、过期或无 admin 角色 Token | 不建立会话 |
| AUTH-UT-08 | Access Cookie登录后访问管理 API | 允许 |
| AUTH-UT-09 | Refresh Cookie轮换 | 新 Token 均验证成功后覆盖 Cookie |
| AUTH-UT-10 | Refresh Token 无效 | 401 并清除 Cookie |
| AUTH-UT-11 | 登出 | 调用 revoke 并清除 Cookie |
| AUTH-UT-12 | Identity revoke 不可用时登出 | 仍清除 Cookie并返回 200 |
| AUTH-UT-13 | `/health`、静态入口和 SPA fallback | 匿名可访问 |
| AUTH-UT-14 | internal GET 缺少或使用错误服务密钥 | 401 |
| AUTH-UT-15 | internal GET 使用正确服务密钥 | 允许 |
| AUTH-UT-16 | 管理员 Cookie调用 internal GET | 401 |
| AUTH-UT-17 | Identity 请求 JSON | 字段为 `grantType`、`username`、`password` / `refreshToken` |

测试使用固定 RSA 测试密钥和受控 Identity HTTP handler，不依赖真实密码或生产密钥。

## QuestionBank 回归

- 可查询全部 `status=parsed` 的解析记录。
- 可分页查询指定 parse 的 blocks。
- 可读取指定 image。
- 不再返回 `importStatus`、`importedAt`，不再接受 `includeImported`。
- 不存在任何导入状态写接口。
- DocLibrary 不创建或更新 `document_parse_imports`。

## 前端验证

当前前端未配置单元测试框架，不为本功能引入新 npm 依赖。必须执行：

```bash
cd src/services/ruoyu.doclibrary/frontend
npm run build
```

并手工验证：首次打开显示登录页、错误密码、普通账户拒绝、管理员登录、自动刷新、401/403 跳转和退出。

## 项目验证

```bash
cd src/services/ruoyu.doclibrary
dotnet test src/Tests/Ruoyu.Study.DocLibrary.Tests/Ruoyu.Study.DocLibrary.Tests.csproj --configuration Release
dotnet build src/Ruoyu.Study.DocLibrary.sln --configuration Release
```

真实 Identity 可用时，额外按 `docs/development/verification.md` 执行 Cookie 会话 smoke test。
