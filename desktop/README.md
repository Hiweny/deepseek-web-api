# DeepSeek Web API · Windows 桌面版

把 **chat.deepseek.com 官网**封装成本机 / 局域网可用的 **OpenAI 兼容接口**。
与仓库里的 Android 版（APK）**功能对齐、代码完全隔离**（Android 代码只动 `app/`，桌面版只动 `desktop/`，互不影响）。

- 运行时：**.NET 8 + WinForms + Microsoft Edge WebView2**（Chromium 内核，质量与兼容性优于旧 IE 内核 WebBrowser / 旧 Electron 方案）
- 发布形态：**单文件自包含 exe**（用户机无需安装 .NET 运行时）
- 版本：跟随 APK（当前 **v1.0.5**，含嵌套 JSON 转义修复与「正文标记不吞内容」修复）

---

## 功能

| 功能 | 说明 |
|---|---|
| 无状态转发 | 每次把完整历史（ChatML）发到同一次官网会话，默认开启 |
| 工具调用 | 模型输出文本标记 → 打包成 OpenAI `tool_calls`（含多层转义对齐 / 宽松解析 / 正文不吞内容） |
| 思考与搜索 | `auto / on / off` 三档，可用 `reasoning_effort` 覆盖 |
| 会话上下文监控 | 按 **1,000,000 tokens** 上限估算，达 **70%** 自动新建对话（旧会话保留，由你自行清理）+ 可手动重置 |
| 局域网访问 | 监听 `0.0.0.0`（**无需管理员权限**），同网段设备用 `http://<你的IP>:8787/v1` 接入 |
| 常驻托盘 | 关闭窗口即最小化到托盘，服务继续运行；双击托盘图标恢复 |
| 保活 | 60 秒看门狗：HTTP 服务/网页异常自动拉起；可开机自启 |
| 日志 | `%LOCALAPPDATA%\DeepSeekWebAPI\app.log`，界面内可查看/复制/打开 |

## 使用

1. 运行 `DeepSeekWebAPI.exe`（首次运行 Windows 可能提示 SmartScreen → 更多信息 → 仍要运行）
2. 左侧点 **对话页**，登录 DeepSeek 官网（登录态与接口共用同一会话）
3. 控制台查看 `Base URL` 与 `API Key`（默认 `sk-deepseek`）
4. 在客户端（Operit / Cherry Studio / 任意 OpenAI SDK）中填写：

```
Base URL: http://127.0.0.1:8787/v1     # 局域网设备改成 http://<主机IP>:8787/v1
API Key : sk-deepseek
Model   : deepseek
```

## 常见问题

- **提示缺少 WebView2 运行时**：Win11 / 新版 Win10 已内置；缺失时点界面上的「打开 WebView2 下载页」安装
  Evergreen 运行时后重启即可（`https://go.microsoft.com/fwlink/p/?LinkId=2124703`）。
- **局域网连不上**：点「放行防火墙（需管理员）」或手动执行
  `netsh advfirewall firewall add rule name="DeepSeek Web API" dir=in action=allow protocol=TCP localport=8787`
- **双击没反应**：先看 `%LOCALAPPDATA%\DeepSeekWebAPI\app.log`（所有启动流程与异常都会写日志）；程序是单实例，
  第二次启动只会唤起已有窗口；若最小化到了托盘，双击托盘图标即可。
- **端口被占用**：程序会自动顺延 +1…+19，实际端口见控制台/托盘提示。

## 从源码构建

```bash
cd desktop/DSWebApi.Desktop
dotnet build -c Release                    # 编译（非 Windows 主机需 EnableWindowsTargeting，已在 csproj 开启）
dotnet run -c Release -- --selftest        # 引擎自检（解析/转义/正文不吞内容，16 项）
dotnet publish -c Release -o publish       # 生成单文件 exe（win-x64）
```

CI：`.github/workflows/desktop.yml`（`windows-latest`）会依次执行
**编译 → 引擎自检 → 单文件发布 → 真机冒烟测试（启动 + `/health` + 鉴权 + 未登录 503）→ 上传产物 / 打 tag 发 Release**。

## 目录

```
desktop/DSWebApi.Desktop/
  Program.cs            入口：单实例、全局异常兜底、日志、先起 HTTP 服务
  Json.cs               极简 JSON（有序、严格解析，语义对齐 JSON.parse / org.json）
  SelfTest.cs           --selftest 引擎自检
  Core/
    LooseJson.cs        宽松 JSON 解析器（带回溯，修复多层转义）  ← 与 APK 1:1
    OpenAiAdapter.cs    文本 → OpenAI tool_calls                  ← 与 APK 1:1
    PromptBuilder.cs    请求 → 注入官网的提示词（规则 1-10）      ← 与 APK 1:1
    ChatEngine.cs       HTTP 路由 / 聊天 / 流式 / 上下文监控      ← 对应 ApiService
    HttpServer.cs       TcpListener HTTP/1.1 + SSE（0.0.0.0）
    WebBridge.cs        与 bridge.js 的通道（对应 DeepSeekController）
    Downloader.cs / Prefs.cs / Log.cs / AutoRun.cs
  UI/
    MainForm.cs         侧边导航 + 控制台/对话页/设置/日志 + 托盘
    InputBox.cs
  Assets/bridge.js      与 APK 同一份注入脚本（复用，仅换消息通道）
  app.ico               程序图标（沿用 APK 的 launcher 图标，保证品牌一致）
```

> WebView2 上的桥接：注入 shim 把 `bridge.js` 的 `window.DSB.onEvent(JSON)` 转发到
> `chrome.webview.postMessage`，原生用 `WebMessageReceived` 接收、`ExecuteScriptAsync` 下发，
> 与 Android 的 `@JavascriptInterface` / `evaluateJavascript` 一一对应。
