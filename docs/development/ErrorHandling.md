# 错误处理规范

## HTTP 状态码使用规则

HTTP 端点实现中必须使用标准的 HTTP 状态码，不得自定义状态码：

| HTTP 状态码 | 使用场景 | 示例 |
|-------------|----------|------|
| `400 Bad Request` | 请求参数验证失败 | ID 格式无效、必填字段为空 |
| `404 Not Found` | 请求的资源不存在 | 学生不存在、错题不存在 |
| `409 Conflict` | 资源已存在（创建时冲突） | 重复提交 |
| `422 Unprocessable Entity` | 业务前置条件不满足 | 审核非待审核状态的错题 |
| `403 Forbidden` | 权限不足 | 越权访问 |
| `401 Unauthorized` | 认证失败 | Token 无效或过期 |
| `500 Internal Server Error` | 服务内部错误 | 数据库异常、未预期的错误 |
| `503 Service Unavailable` | 服务不可用 | 依赖服务宕机 |

## 错误信息规范

1. 错误信息使用中文，因为调用方需要将信息展示给终端用户
2. 错误信息应简洁明确，不包含技术细节
3. 同一类错误在各服务中使用相同的措辞

## 错误响应格式

所有 HTTP 端点返回统一的 JSON 错误响应格式：

```json
{
  "success": false,
  "message": "错误描述信息",
  "errorCode": "ERROR_CODE"
}
```

- 领域异常映射为对应的 HTTP 状态码（400 / 404 / 409 / 422）
- 其他未捕获异常统一返回 500，错误信息脱敏

## 参数验证

参数验证应在 HTTP 端点方法入口处进行，尽早返回错误。

## 日志规范

- 使用结构化日志占位符，不要使用字符串插值
- 异常对象必须传入：使用 `LogError(ex, ...)` 而非 `LogError(ex.Message, ...)`
- 预期内的 NotFound 使用 Warning 级别
- 不记录密码、Token、手机号等敏感信息

### Serilog + Loki 日志系统

DocLibrary 使用 Serilog 替代原生 Microsoft.Extensions.Logging，双写到 Console + Grafana Loki。

配置入口：`Program.cs` 中 `UseAgentSerilog("Ruoyu.Study.DocLibrary")`，读取 `appsettings.json` 中 `Serilog` 配置节。

Loki 地址通过短环境变量 `LOKI_URI` 注入，`Program.cs` 启动时读取并覆盖 `Serilog:WriteTo:1:Args:uri` 配置键：

| 环境变量 | 示例值 | 说明 |
|----------|--------|------|
| LOKI_URI | http://ruoyu-loki:3100 | Loki 地址（Docker 部署时必须设置） |

> **容错机制**：如果 `LOKI_URI` 未设置，Loki Sink 使用 appsettings.json 中的 fallback 地址。Loki 不可达时 Sink 异步重试，不影响服务启动。

### 敏感字段脱敏

写入日志（含 Loki）前，必须对以下字段脱敏。日志最终会进入 Loki 仪表盘，明文敏感信息会违反合规要求：

| 字段类型 | 脱敏规则 | 示例 |
|----------|---------|------|
| LLM ApiKey / MinerU ApiToken | 保留前 4 + 后 4，中间用 `****` 替换；长度不足 8 位时全部替换为 `****` | `sk-a****1b2c` |
| OSS AccessKey / SecretKey | 完全不记录 | — |

实现位置：`Ruoyu.Study.DocLibrary.Service.SensitiveDataMasker` 静态工具类。业务代码中使用 `_logger.LogInformation("... ApiKey={ApiKey}", SensitiveDataMasker.MaskApiKey(apiKey))` 形式调用。

> 数据库字段不受此规则约束，仍按业务需要存储原始值；该规则仅约束日志输出。

### CorrelationId 流转

CorrelationId 适用于 HTTP 路径，便于在 Loki 中跨服务追踪请求链路：

- HTTP 路径：由 `CorrelationIdMiddleware`（ASP.NET Core 中间件）从请求头 `x-correlation-id` 读取或新建，写入 `HttpContext.Items` 并通过 `BeginScope` 注入日志上下文；响应头回写 `x-correlation-id` 便于调用方关联。

HTTP 控制器（`DocumentFileEndpoints` / `AuthEndpoints` 等）必须在该中间件作用范围内。

中间件管道位置（`Program.cs` 中注册顺序）：

```
UseMiddleware<CorrelationIdMiddleware>()   ← 必须在 Auth 之前
  → UseAuthentication()
  → UseAuthorization()
  → UseDefaultFiles() / UseStaticFiles()
  → MapEndpoints
```

`CorrelationIdMiddleware` 必须在认证之前注册，确保所有下游中间件（含认证失败响应）都在 CorrelationId scope 内。
