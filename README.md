# DeepSeek Web API

把 **chat.deepseek.com 官网**（纯网页层）封装成 **OpenAI 兼容接口**的 Android 应用与 Windows 桌面程序。

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
- **上下文监控**：按官网 1M 上限跟踪当前会话累计上下文（默认 70% 阈值），达阈值自动新建对话（旧会话保留，自行决定是否清理）
- **工具调用容错**：嵌套 / 字符串化 JSON 的多层转义自动对齐（少转义、多转义、arguments 整体字符串化都能还原成严格合法的 JSON）；正文里出现的工具标记不会被误判为工具调用，正文内容不会被吞

---

## 🖥 Windows 桌面版（含**多账号轮换**）

到 [Releases](https://github.com/Hiweny/deepseek-web-api/releases) 下载 `DeepSeekWebAPI-Desktop-*-win-x64.exe`（单文件，无需安装）。

### 为什么需要多账号轮换
网页端对**单个账号**有发送频率限制：客户端工具调用密集时会提示「消息发送频繁，请稍后重试」（临时不可发，约半小时恢复，一般不封号）。
把请求分散到多个账号上，可以显著降低单账号压力。

### 使用
1. 双击运行；左侧「控制台」→ 复制 Base URL / API Key 填到客户端
2. 左侧「对话页」登录你的 DeepSeek 账号（登录态本地保存）
3. 想多账号轮换：侧栏「账号 · 多开轮换」→ **＋ 新建账号** → 在新窗口里登录第二个账号（可加任意多个）
4. 控制台「多账号轮换」卡片 → **开关轮换**

### 轮换规则（严格顺序，不是"每小时配额"）
- **严格按顺序轮流**：账号 1 → 2 → 3 → … → 1，不做"空闲最久优先"之类的打分
- **每个账号的同一个对话最多连发 N 次**（默认 **2**，控制台「调整次数…」可改，1–50）
- **所有账号都轮完一圈 → 全员新开对话**（不在旧对话上继续，避免串味 / 上下文超限）
- **并发也照样轮换**：同时到达的多个请求在选号时**原子预占**名额，会分散到不同账号，每个名额同样计入连发次数
- 账号命中「消息发送频繁」→ 自动**冷却**（默认 30 分钟），期间跳过它、其他账号继续服务
- 所有账号都在冷却 → 接口返回 `429`（带冷却剩余时间），而不是硬打上游把号打废
- **关闭开关 = 原来的单账号模式**（行为与 v1.0.6 完全一致）

### 上下文与自动新开对话
- 每个账号**各自**统计当前对话的估算 token；达到阈值（默认 1M × 70%）→ 该账号下次调用前自动「新开对话」
- 官网提示「上下文已达上限 / 请开启新对话」时，也会自动新开对话并重试（非流式），请求不会白失败

### 界面
- 控制台：服务状态磁贴 + 接口信息 + 多账号轮换 + **账号调用情况表**（每个账号的当前对话已发次数 / 累计调用 / 最近使用）
- 侧栏：账号列表（含状态）+ 添加账号 + 设置/退出，深色主题（含系统标题栏）

### 升级 / 覆盖更新
单文件绿色版：下载新 exe → 关闭本程序 → **直接覆盖旧 exe** 即可。
登录态、设置、各账号 WebView2 数据目录都在 `%LOCALAPPDATA%\DeepSeekWebAPI`，覆盖 exe 不受影响。
控制台「常用操作 → 检查更新」可一键比对最新版本并跳转下载。

### 账号隔离
每个账号使用**独立的 WebView2 数据目录**（独立浏览器进程），因此 Cookie / localStorage / 缓存 / **数美设备指纹（device_id）** 全部互相独立
（已实测：3 个实例的 device_id 去重后 3 个各不相同，`--probe-device` 可复现）。

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
  达到阈值时（默认 70% ≈ 70 万 tokens）自动点击「新建对话」，旧会话保留在侧边栏由你自行清理。
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
