# DeepSeek Web API · Android

把 **chat.deepseek.com 官网**（纯网页层）封装成本机的 **OpenAI 兼容 HTTP 接口**，装进一个 APK 里。

> 不破解、不伪造、不走底层私有 API：App 内部用一个真实 WebView 打开并登录你自己的 DeepSeek 账号，
> 通过 DOM 层注入提示词、点击发送、拦截官网自身的 SSE 响应流，再把结果转成标准 OpenAI 协议对外提供。
> 登录态、模式开关、附件上传全部沿用官网。

---

## 架构

```
外部客户端（ChatBox / Open WebUI / 任意 OpenAI 兼容工具 / curl）
        │  HTTP  http://127.0.0.1:8787/v1
        ▼
┌───────────────────────────────────────────────┐
│                  APK（前台服务）               │
│  HttpBridgeServer（本地 HTTP/SSE 服务）        │
│        │  解析请求 → 构建注入文本               │
│        ▼                                       │
│  DeepSeekController（原生 ↔ JS 通道 reqId）     │
│        │  evaluateJavascript                   │
│        ▼                                       │
│  WebView · chat.deepseek.com（服务持有）        │
│        │  bridge.js：                           │
│        │   · 找输入框 → 注入 prompt → 点发送     │
│        │   · 钩住 XHR：解析 SSE、思考/正文分离    │
│        │   · 防撤回：撤回前缓存真实内容           │
│        │   · 附件：base64 → File → input[type=file]│
│        ▼                                       │
│  OpenAI 兼容响应（流式 / 非流式 + tool_calls）   │
└───────────────────────────────────────────────┘
```

## 功能

- **OpenAI 兼容**：`/v1/chat/completions`（流式与非流式）、`/v1/models`、`/health`。
- **模型映射**：对外只有 `deepseek`（含 `deepseek-chat` / `deepseek-reasoner` 别名）。
- **思考/正文分离**：官网 DeepThink 的思考过程映射为 `reasoning_content`，正文映射为 `content`。
- **工具调用**：按 ds-free-api 同款方案，通过提示词注入工具定义与格式，解析 `<|tool▁calls▁begin|>…<|tool▁calls▁end|>` 输出为 `tool_calls`。
- **多轮对话**：复用官网会话上下文；每轮仅注入新增内容（也可切「无状态模式」发全量）。
- **图片 / 文件**：支持 `image_url`（data URL 或 http）与 file 内容块，自动挂到官网输入区。
- **防撤回**：回复被官方撤回 / 内容过滤时，依旧返回本地缓存的真实内容。
- **后台保活**：前台常驻服务 + WakeLock + 9 分钟心跳 + 看门狗自愈 + 无障碍保活 + 开机自启 + 电池白名单。
- **UI**：底部导航双页（控制面板 / 对话页），全局深色模式（含官网页面随系统主题）。

## 使用

1. 安装 APK，打开 App（首次会自动启动服务并加载官网）。
2. 切到「对话页」，登录你的 DeepSeek 账号（登录态会持久保存）。
3. 「控制台」查看 `Base URL`（默认 `http://127.0.0.1:8787/v1`）与 `API Key`（默认 `sk-deepseek`）。
4. 在任意 OpenAI 兼容客户端里填入上面两项即可。

```bash
curl http://127.0.0.1:8787/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sk-deepseek" \
  -d '{"model":"deepseek","messages":[{"role":"user","content":"你好"}],"stream":true}'
```

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

- HTTP 服务仅监听 `127.0.0.1`，不暴露公网。
- 登录态只存在于本机 WebView Cookie，不上传任何第三方。
- 仅供学习与个人效率使用，请遵守 DeepSeek 服务条款。

## 构建

```bash
./gradlew :app:assembleRelease
# 产物：app/build/outputs/apk/release/app-release.apk
```

推送 `v*` 标签会自动构建并发布 Release（未配置签名密钥时回退 debug 签名）。
