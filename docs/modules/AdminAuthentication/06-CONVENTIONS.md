# 06-CONVENTIONS — DocLibrary 管理员认证约定

## 路径

| 类型 | 前缀 |
|------|------|
| 浏览器认证 | `/admin/auth` |
| 浏览器管理 API | `/admin` |
| QuestionBank 服务间查询 | `/internal/question-bank` |
| 匿名健康检查 | `/health` |

## 策略与配置命名

| 名称 | 值 |
|------|----|
| 管理员授权策略 | `DocLibraryAdmin` |
| QuestionBank 策略 | `QuestionBankService` |
| Access Cookie | `doclibraryAccessToken` |
| Refresh Cookie | `doclibraryRefreshToken` |
| 服务密钥请求头 | `X-DocLibrary-Service-Key` |
| Authority 配置 | `IdentityService:Authority` |
| Issuer 配置 | `IdentityService:Issuer` |
| Audience 配置 | `IdentityService:Audience` |
| Cookie Secure 配置 | `Authentication:CookieSecure` |
| QuestionBank 密钥配置 | `InternalAuth:QuestionBankKey` |

## 错误与日志

- 对外认证错误使用英文、简洁、稳定的消息。
- 401 表示无有效身份；403 表示身份有效但没有管理员角色。
- 错误码使用 `DOCLIBRARY_` 前缀。
- 不记录密码、Access Token、Refresh Token、Cookie、AppSecret 或内部服务密钥。
- Identity 失败只记录 HTTP 状态、关联 ID 和通用失败类别。

## 前端

- 不使用 localStorage/sessionStorage 保存认证材料。
- API 模块必须复用单一共享 Axios 实例。
- 认证端点的 401 不触发递归 refresh。
- 403 不自动重试。
- 登录表单必须使用 `autocomplete="username"` 和 `autocomplete="current-password"`。
