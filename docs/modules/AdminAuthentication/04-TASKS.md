# 04-TASKS — DocLibrary 管理员认证任务

| 编号 | 任务 | 验收结果 |
|------|------|----------|
| AUTH-01 | 增加 Identity 认证客户端、显式 Token 验证器和配置模型 | 登录/刷新 Token 在设置 Cookie 前完成完整验证 |
| AUTH-02 | 增加 login、refresh、logout、session 端点 | HTTP 契约符合 02-SPEC |
| AUTH-03 | 配置 JwtBearer Cookie 回退和 `DocLibraryAdmin` 策略 | `/admin/*` 满足 401/403 矩阵 |
| AUTH-04 | 为现有浏览器管理 route group 应用管理员策略 | 所有管理读写端点均受保护 |
| AUTH-05 | 移除无调用方的 Quaestura（原 QuestionBank） internal API | 无 `/internal/question-bank/*`，无导入状态写回 |
| AUTH-06 | 移除 Quaestura（原 QuestionBank） 服务密钥策略与配置 | `start.sh` 和 `appsettings.json` 无 `QuestionBankKey` |
| AUTH-07 | 增加前端登录、会话、刷新和退出逻辑 | 前端不读取或保存 Token |
| AUTH-08 | 更新 Docker/Consul/启动配置说明 | Identity 共享配置来自 Consul，保持单镜像和静态托管 |
| AUTH-09 | 增加后端认证与内部接口测试 | 覆盖 05-TESTS 的必需场景 |
| AUTH-10 | 执行后端测试、前端构建和可行的 Identity 集成验证 | 以实际命令输出为准 |
