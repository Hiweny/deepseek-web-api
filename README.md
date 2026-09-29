# DeepSeek Web API

把 **chat.deepseek.com 官网**（纯网页层）封装成 **OpenAI 兼容接口**的 Android 应用。

> 不破解、不伪造、不走底层私有 API：程序内部用真实浏览器内核（WebView）打开并登录你自己的
> DeepSeek 账号，通过 DOM 层注入提示词、点击发送、拦截官网自身的 SSE 响应流，再把结果转成
> 标准 OpenAI 协议。登录态、模式开关、附件上传全部沿用官网。

---

## 能力

- **OpenAI 兼容**：`/v1/chat/completions`（流式 / 非流式）、`/v1/models`、`/health`
- **模型映射**：对外只有 `deepseek`（含 `deepseek-chat` / `deepseek-reasoner` 别名）
- **思考 / 正文分离**：官网 DeepThink 的思考 → `reasoning_content`，正文 → `content`
- **工具调用**：提示词注入工具定义与格式，解析 `<|tool▁calls▁begin|>…<|tool▁calls▁end|>` → 标准 `tool_calls`
- **多轮对话**：复用官网会话上下文（每轮仅注入新增内容），可选无状态模式
- **图片 / 文件**：支持 `image_url`（data URL 或 http）与 file 内容块，挂载到官网输入区
- **防撤回**：回复被官方撤回 / 内容过滤时，依旧返回本地缓存的真实内容
- **上下文监控**：按官网 1M 上限跟踪当前会话累计上下文（默认 70% 阈值），达阈值自动新建对话并尽力删除旧会话

---

## 📱 Android 版（APK）

### 下载与安装
到 [Releases](https://github.com/Hiweny/deepseek-web-api/releases) 下载 `DeepSeekWebAPI-x.x.x.apk` 安装。

### 使用
1. 安装并打开 APK（自动启动服务并加载官网）
2. 切「对话页」登录你自己的 DeepSeek 账号
3. 「控制台」复制 **Base URL**（默认 `http://127.0.0.1:8787/v1`）与 **API Key**（默认 `sk-deepseek`）

```bash
curl http://127.0.0.1:8787/v1/chat/completions \
  -H "Content-Type: application/json" -H "Authorization: Bearer sk-deepseek" \
  -d '{"model":"deepseek","messages":[{"role":"user","content":"你好"}],"stream":true}'
```

> APK 的 HTTP 服务仅监听 `127.0.0.1`，只能被**同一台手机**上的 App 调用。

### 界面
底部导航双页面：**控制台**（服务状态、地址与密钥、调用统计、设置）/ **对话页**（DeepSeek 官网 WebView）。
全局深色模式，官网页面自动跟随系统主题。

### 会话与上下文

- **默认无状态模式**：每次请求把完整对话注入官网输入框，不依赖官网会话记忆。
- **上下文监控**：按「1M 上限 × 阈值」估算当前会话已用上下文（中日韩字符 ≈1.35 token/字，其余 ≈4 字符/token）。
  达到阈值时（默认 70% ≈ 70 万 tokens）自动点击「新建对话」，并尽力通过页面 DOM 删除旧会话；
  删除失败不影响主流程，控制台「运行日志」会写明结果。
- 控制台「会话上下文」实时显示用量；可调整「上下文上限（tokens）」「新对话阈值（%）」，或关闭「自动新开对话」。

### 保活
前台常驻通知 + 唤醒锁保证 HTTP 服务存活，通知栏实时显示 API 调用情况；可开启开机自启、
忽略电池优化；内置无障碍服务，在服务被系统回收 / 卡死时自动拉起。

---

## 架构

```
外部客户端（ChatBox / Open WebUI / 任意 OpenAI 兼容工具 / curl）
        │  HTTP（仅本机 127.0.0.1）
        ▼
App 内 HTTP 服务（OpenAI 兼容路由 + SSE 流式）
        │  解析请求 → 构建注入文本
        ▼
原生 ↔ 页面 通道（reqId 关联；注入命令 / 回传事件）
        ▼
Android WebView · chat.deepseek.com
        │  bridge.js：
        │   · 找输入框 → 注入 prompt → 点发送
        │   · 钩住 XHR：解析 SSE、思考/正文分离
        │   · 防撤回：撤回前缓存真实内容
        │   · 附件：base64 → File → input[type=file]
        ▼
OpenAI 兼容响应（流式 / 非流式 + tool_calls）
```

## 自行构建

```bash
./gradlew :app:assembleRelease
# 产物：app/build/outputs/apk/release/app-release.apk
```

推送 `v*` 标签会触发 GitHub Actions 自动构建并发布 APK。

## 权限

| 权限 | 用途 |
|------|------|
| INTERNET | 加载官网 |
| FOREGROUND_SERVICE(_DATA_SYNC) | 前台常驻服务，保持 HTTP 接口存活 |
| POST_NOTIFICATIONS | 常驻通知显示 API 调用情况 |
| WAKE_LOCK / RECEIVE_BOOT_COMPLETED | 后台唤醒与开机自启 |
| REQUEST_IGNORE_BATTERY_OPTIMIZATIONS | 防被系统省电杀后台 |
| 无障碍（可选） | 检测服务被回收/卡死时自动拉起 |

## 安全与声明

- HTTP 服务仅监听 `127.0.0.1`，不对局域网/公网开放。
- 登录态只存在于本机 WebView Cookie，不上传任何第三方。
- 仅供学习与个人效率使用，请遵守 DeepSeek 服务条款。

## 许可证

MIT
