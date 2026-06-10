# .NET 解决方案编码规范

> 本文档为解决方案级共享规范，与 `ruoyu.mistake`、`ruoyu.student` 等服务共用同一份。

详见 [ruoyu.mistake/docs/DotNetCodingPolicy.md](../../../ruoyu.mistake/docs/overview/DotNetCodingPolicy.md)（唯一事实源）。

## 本服务特定补充

| 规范项 | DocRetrieval 实际做法 |
|--------|----------------------|
| 测试框架 | xUnit + Moq + FluentAssertions |
| 对象映射 | Mapster |
| 数据库初始化 | 原生 SQL（DatabaseInitializer），不使用 EF Core Migration |
| 日志语言 | 中文（`_logger.LogInformation("文档已删除：{Title}", title)`） |
| 错误码前缀 | `DOCRETRIEVAL_` |
| 注释语言 | 中文（与 DotNetCodingPolicy 第 4.4 节的"英文注释"规范不一致，以实际代码为准） |

> [推断] 本服务的日志和注释使用中文，与 DotNetCodingPolicy 4.4 节"注释和字符串必须使用英文"的要求不一致。建议后续统一。