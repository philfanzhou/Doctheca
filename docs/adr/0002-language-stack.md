# 2. 语言栈选择

## 状态

已决定 (Accepted)

## 上下文

DocRetrieval 服务需要：
1. 解析 PDF/Word/PPT（扫描件走 OCR）→ .NET 生态工具链可胜任（iTextSharp/OpenXML/Tesseract.NET）
2. 提供 gRPC 查询接口 → 现有微服务均为 .NET，保持一致性
3. 提供 Web 管理界面 → 与现有技术栈一致
4. 语义召回需要 embedding 模型 → 可调用外部模型服务，不要求 .NET 本身做推理

## 决策

选择 **全 .NET 方案**。

选项：
1. ~~**混合方案**~~ - .NET 作为主服务 + Python 负责文档解析/索引
2. ~~**全 Python**~~ - 全部用 Python 实现（FastAPI + gRPC）
3. ~~**全 .NET**~~ - 全部用 .NET 实现 ✅ 已选

## 备选方案分析

### 方案 A：混合方案（.NET + Python）
- **优点**：
  - 发挥各自生态优势：Python 做解析/AI，.NET 做服务/编排
  - 与现有微服务技术栈一致（gRPC 协议统一）
- **缺点**：
  - 部署复杂度稍高（两个容器）
  - 需要进程间通信机制（队列/内部 API）

### 方案 B：全 Python
- **优点**：
  - 单一技术栈，部署简单
  - Python AI 生态成熟
- **缺点**：
  - 与现有 .NET 微服务技术栈不一致
  - 团队可能需要额外学习成本

### 方案 C：全 .NET ✅
- **优点**：
  - 与现有技术栈完全一致
  - 单一技术栈，部署简单
  - 文档解析（iTextSharp/OpenXML）、OCR（Tesseract.NET）均可胜任
  - Embedding 可调用外部模型服务（如 Ollama），不依赖 .NET 推理
- **缺点**：
  - PDF/PPT 解析能力略逊于 Python 生态，但英文场景足够
  - OCR 精度不如 PaddleOCR，但 Tesseract.NET 英文识别率可接受

## 理由

选择全 .NET 是因为：
- 与现有微服务技术栈完全一致，降低团队学习成本
- 项目仅需英文文档处理，.NET 生态工具链（iTextSharp/OpenXML/Tesseract.NET）可胜任
- Embedding 能力通过外部模型服务提供，不依赖 .NET 本身推理
- 部署简单（单容器），运维成本低
- 只有在扫描件质量极差或需要端到端模型推理时才必须用 Python，当前场景不满足这些条件

## 后果

- .NET 服务需集成 iTextSharp（PDF 解析）、OpenXML（Word/PPT 解析）、Tesseract.NET（OCR）
- 需要部署/接入外部 embedding 模型服务
- 所有能力集中在单一 .NET 服务中，架构简单
- 未来如需更强解析/OCR 能力，可独立抽离 Python 解析服务
