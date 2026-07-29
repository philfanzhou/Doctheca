# 06-CONVENTIONS — QuestionBank 只读解析数据接口约定

## 路由与认证

- 服务间路径统一使用 `/internal/question-bank`。
- 服务密钥请求头固定为 `X-DocLibrary-Service-Key`。
- 配置键固定为 `InternalAuth:QuestionBankKey`。
- 管理员 JWT 与服务密钥不能互相替代。

## 数据所有权

- DocLibrary 拥有文档、parse、block 和 parse image。
- QuestionBank 拥有题目、拆题结果和导入幂等状态。
- DocLibrary 不保存 QuestionBank 题目 ID或导入状态。
- QuestionBank 使用 DocLibrary `parseId` 作为外部来源标识，不对 DocLibrary 数据库建外键。

## HTTP

- 服务间接口只使用 GET。
- 错误码继续使用 `DOCLIBRARY_` 前缀。
- 分页响应保持 `{success,data,total,page,pageSize,totalPages}`。
- 服务密钥缺失或错误统一返回 401，不区分具体原因。
