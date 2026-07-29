# 01-FEATURE — DocLibrary 管理员认证

## 功能概述

DocLibrary 管理后台使用 QuantumZhou.Identity 的 bootstrap 管理员账户登录。浏览器管理接口只接受签名有效且包含 `role=admin` 的 Identity JWT；普通 Identity 用户即使账号密码正确也不能建立 DocLibrary 管理会话。

管理前端继续由 DocLibrary Host 静态托管并与后端同源部署。Access Token 和 Refresh Token 仅存放在 HttpOnly Cookie 中，不返回给前端 JavaScript，也不写入 localStorage。

## 用户故事

> 作为 DocLibrary 管理员，我希望使用 Identity 管理员账户登录并安全地维持会话，以便只有经过 Identity 验证的管理员能够查看或变更 DocLibrary 数据。

## 验收条件

| 编号 | 验收条件 |
|------|----------|
| AC-01 | 未登录访问除认证端点外的 `/admin/*` 返回 401 |
| AC-02 | 有效普通用户 JWT 访问 `/admin/*` 返回 403 |
| AC-03 | Identity bootstrap 管理员使用正确密码登录成功 |
| AC-04 | 错误用户名或密码返回统一 401，不泄露账户存在性或密码校验细节 |
| AC-05 | 正确普通账户登录返回统一 403，不建立 Cookie 会话 |
| AC-06 | 登录代理使用 `grantType`、`username`、`password` camelCase 字段调用 `POST /api/auth/token` |
| AC-07 | DocLibrary 在设置 Cookie 前验证 JWT 签名、issuer、audience、有效期和 `role=admin` |
| AC-08 | Access Token 与 Refresh Token 仅存放在 HttpOnly、SameSite=Strict Cookie 中 |
| AC-09 | Access Token 过期后可通过 Refresh Cookie 完成一次性令牌轮换 |
| AC-10 | 登出调用 Identity 撤销 Refresh Token，并无条件清除本地 Cookie |
| AC-11 | SPA、登录页、静态文件和 `/health` 保持匿名可访问 |
| AC-12 | 日志不记录密码、JWT、Refresh Token、Cookie、服务密钥或 Identity AppSecret |

## 范围

### 范围内

- Identity 密码登录代理、JWT 管理员角色验证、Cookie 会话、刷新和登出
- `/admin/*` 管理端点的管理员授权策略
- 前端登录页、启动会话检查、自动刷新、401/403 处理和退出入口
- Identity Authority、Cookie 安全属性和可选 AppId/AppSecret 部署配置
- 后端认证/授权测试与前端构建验证

### 范围外

- 修改 Identity 的 `AdminBootstrap` 植入或 `role=admin` 注入逻辑
- 在 DocLibrary 创建或管理 Identity 用户
- 在前端保存或展示任何 Token
- 为 QuestionBank 内部查询复用管理员 Cookie

## 关联文档

- [02-SPEC.md](./02-SPEC.md)
- [03-DESIGN.md](./03-DESIGN.md)
- [QuestionBankImport](../QuestionBankImport/01-FEATURE.md)
