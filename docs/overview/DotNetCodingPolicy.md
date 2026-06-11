# .NET 解决方案编码规范

> 本文档为解决方案级共享规范，与 `ruoyu.mistake`、`ruoyu.student` 等服务共用同一份。

详见 [ruoyu.mistake/docs/DotNetCodingPolicy.md](../../../ruoyu.mistake/docs/overview/DotNetCodingPolicy.md)（唯一事实源）。

## 本服务特定补充

| 规范项 | DocRetrieval 实际做法 |
|--------|----------------------|
| 测试框架 | xUnit + Moq + FluentAssertions |
| 对象映射 | Mapster |
| 数据库初始化 | 原生 SQL（DatabaseInitializer），不使用 EF Core Migration |
| 日志语言 | 英文（遵循 DotNetCodingPolicy 4.4 节） |
| 错误码前缀 | `DOCRETRIEVAL_` |
| 注释语言 | 英文（遵循 DotNetCodingPolicy 4.4 节） |
| 域值例外 | 数据库域值常量（如 `SubjectEnglish = "英语"`、`GradeLabels`）保留中文，因为它们是业务数据的实际存储值，不是日志/注释/输出字符串 |