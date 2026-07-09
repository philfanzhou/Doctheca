# DocLibrary Admin Frontend

这是 DocLibrary 文档检索服务的管理后台前端。

## 开发

```bash
cd frontend
npm install
npm run dev
```

## 构建

```bash
npm run build
```

## 预览

```bash
npm run preview
```

## 前端规范

### 技术栈

| 项目 | 技术 |
|------|------|
| 框架 | Vue 3.5 + TypeScript |
| UI | 纯手写 CSS（不使用 UI 组件库） |
| HTTP 客户端 | Axios |
| 构建工具 | Vite |

### 响应式设计要求

**必须保证在低分辨率（1280x720 及以上）下的可用性：**

1. **弹窗/对话框**
   - 必须设置 `max-height: calc(100vh - 32px)`
   - 内容区域必须支持 `overflow-y: auto` 滚动
   - 头部和底部使用 `flex-shrink: 0` 固定
   - 确保操作按钮始终可见可点击

2. **表格**
   - 在窄屏下使用水平滚动
   - 关键操作列固定可见

3. **表单**
   - 输入框使用 `width: 100%` 适配容器
   - 下拉菜单避免超出视口

### 测试矩阵

| 分局率 | 测试重点 |
|--------|---------|
| 1920x1080 | 基础布局、表格完整显示 |
| 1280x720 | 弹窗滚动、按钮可点击 |
| 1366x768 | 常见笔记本分辨率兼容 |
