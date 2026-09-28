# DeepSeek Web API

把 **chat.deepseek.com 官网**（纯网页层）封装成 **OpenAI 兼容接口**。包含两个独立客户端，共用同一套「浏览器 DOM 桥接」内核：

| 产物 | 目录 | 说明 |
|---|---|---|
| 📱 **Android APK** | 仓库根目录（`app/`） | 手机端，接口监听 `127.0.0.1`，仅供本机 App 调用 |
| 💻 **Windows EXE** | [`desktop/`](desktop/) | 电脑端，可监听局域网，让同网络设备都能调用 |

> 不破解、不伪造、不走底层私有 API：程序内部用真实浏览器内核打开并登录你自己的 DeepSeek 账号，
> 通过 DOM 层注入提示词、点击发送、拦截官网自身的 SSE 响应流，再把结果转成标准 OpenAI 协议。
> 登录态、模式开关、附件上传全部沿用官网。

---

## 共同能力

- **OpenAI 兼容**：`/v1/chat/completions`（流式 / 非流式）、`/v1/models`、`/health`
- **模型映射**：对外只有 `deepseek`（含 `deepseek-chat` / `deepseek-reasoner` 别名）
- **思考 / 正文分离**：官网 DeepThink 的思考 → `reasoning_content`，正文 → `content`
- **工具调用**：提示词注入工具定义与格式，解析 `<|tool▁calls▁begin|>…<|tool▁calls▁end|>` → 标准 `tool_calls`
- **多轮对话**：复用官网会话上下文（每轮仅注入新增内容），可选无状态模式
- **图片 / 文件**：支持 `image_url`（data URL 或 http）与 file 内容块，挂载到官网输入区
- **防撤回**：回复被官方撤回 / 内容过滤时，依旧返回本地缓存的真实内容

---

## 💻 Windows 桌面版（EXE）

电脑作为服务器，开放 API 端口给局域网内所有设备使用。

### 下载与安装
到 [Releases](https://github.com/Hiweny/deepseek-web-api/releases) 下载：
- `DeepSeekWebAPI-Portable-x.x.x.exe` —— **免安装绿色版**，双击即用（推荐）
- `DeepSeekWebAPI-Setup-x.x.x.exe` —— 安装版，可创建开始菜单快捷方式

### 使用
1. 双击运行 → 托盘出现图标（服务已在后台启动）
2. 切到「对话页」登录 DeepSeek 账号
3. 「控制台」查看 **本机地址** 与 **局域网地址**，填入任意 OpenAI 兼容客户端

```bash
# 本机
curl http://127.0.0.1:8787/v1/chat/completions \
  -H "Content-Type: application/json" -H "Authorization: Bearer sk-deepseek" \
  -d '{"model":"deepseek","messages":[{"role":"user","content":"你好"}],"stream":true}'

# 局域网内其它设备（把 IP 换成控制台显示的局域网地址）
curl http://192.168.1.10:8787/v1/chat/completions -H "Authorization: Bearer sk-deepseek" ...
```

### 设置
| 项 | 说明 |
|---|---|
| 端口 | HTTP 监听端口，默认 `8787` |
| API Key | 外部调用需携带的密钥，默认 `sk-deepseek` |
| 超时（秒） | 单次回复等待上限 |
| 思考 / 搜索策略 | 跟随官网（auto）/ 强制开（on）/ 强制关（off） |
| 允许局域网访问 | 开=监听 `0.0.0.0`（其它设备可访问）；关=仅 `127.0.0.1` |
| 无状态模式 | 每次发送完整对话，不依赖官网会话记忆 |
| 开机自启 / 关闭到托盘 / 阻止休眠 | 后台保活相关 |

> 首次开启「允许局域网访问」时，Windows 防火墙会弹窗，请选择「允许访问」。

### 保活
关闭窗口默认最小化到**系统托盘**（服务继续运行）；托盘右键可显示主界面 / 打开对话页 / 复制接口地址 / 退出。
可再开启「开机自启」与「阻止休眠」提升长时间稳定性。

### 常见问题排查
- **窗口黑屏 / 空白**：程序默认已关闭硬件加速以规避部分显卡的纯黑窗口问题；日志写入
  `%APPDATA%\DeepSeekWebAPI\app.log`（控制台「打开日志文件」可直接定位）。若仍异常，可在
  快捷方式目标后加 `--gpu` 反向测试是否为显卡问题。
- **「对话页」一片空白**：点控制台的「重载官网」；若提示加载失败，多半是网络/代理问题，
  可用「浏览器打开官网」确认本机能否访问 `chat.deepseek.com`。
- **局域网其它设备连不上**：确认已开启「允许局域网访问」，并在 Windows 防火墙弹窗中选「允许」。
- **需要更详细排查**：控制台「诊断」区可打开「官网调试 / 界面调试」开发者工具。

---

## 📱 Android 版（APK）

见仓库根目录的构建产物，使用方式：

1. 安装 APK 并打开（自动启动服务并加载官网）
2. 切「对话页」登录 DeepSeek 账号
3. 「控制台」复制 `Base URL`（默认 `http://127.0.0.1:8787/v1`）与 `API Key`

APK 的 HTTP 服务仅监听 `127.0.0.1`，只能被本机 App 调用；若需给其它设备使用，请用电脑版。

---

## 架构

```
外部客户端（ChatBox / Open WebUI / 任意 OpenAI 兼容工具 / curl）
        │  HTTP
        ▼
本地 HTTP 服务（OpenAI 兼容路由 + SSE 流式）
        │  解析请求 → 构建注入文本
        ▼
原生 ↔ 页面 通道（reqId 关联；注入命令 / 回传事件）
        ▼
浏览器内核 · chat.deepseek.com（APK=WebView / EXE=Electron webview）
        │  bridge.js：
        │   · 找输入框 → 注入 prompt → 点发送
        │   · 钩住 XHR：解析 SSE、思考/正文分离
        │   · 防撤回：撤回前缓存真实内容
        │   · 附件：base64 → File → input[type=file]
        ▼
OpenAI 兼容响应（流式 / 非流式 + tool_calls）
```

两个客户端共用同一份 `bridge.js`（页面注入脚本）与同一套协议转换逻辑。

## 自行构建

### Windows EXE
```bash
cd desktop
npm install
npm test              # 纯 Node 逻辑自测（28 项）
npm run dist          # 产物在 desktop/dist/
```

### Android APK
```bash
./gradlew :app:assembleRelease
# 产物：app/build/outputs/apk/release/app-release.apk
```

推送 `v*` 标签构建并发布 APK；推送 `desktop-v*` 标签构建并发布 EXE（两个工作流互不影响）。

## 权限（Android）

| 权限 | 用途 |
|------|------|
| INTERNET | 加载官网 |
| FOREGROUND_SERVICE(_DATA_SYNC) | 前台常驻服务，保持 HTTP 接口存活 |
| POST_NOTIFICATIONS | 常驻通知显示 API 调用情况 |
| WAKE_LOCK / RECEIVE_BOOT_COMPLETED | 后台唤醒与开机自启 |
| REQUEST_IGNORE_BATTERY_OPTIMIZATIONS | 防被系统省电杀后台 |
| 无障碍（可选） | 检测服务被回收/卡死时自动拉起 |

## 安全与声明

- Android 版 HTTP 服务仅监听 `127.0.0.1`；桌面版默认监听局域网（可在设置中关闭）。
- 登录态只存在于本机浏览器 Cookie / Electron 会话，不上传任何第三方。
- 仅供学习与个人效率使用，请遵守 DeepSeek 服务条款。

## 许可证

MIT
