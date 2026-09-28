'use strict';
/**
 * 把 OpenAI Chat Completions 请求转换为「注入官网输入框」的文本，并提取附件。
 * 会话模式（默认）：只发本轮新增内容；无状态模式：发全部消息。
 */

const TOOL_START = '<|tool\u2581calls\u2581begin|>';
const TOOL_END = '<|tool\u2581calls\u2581end|>';
const TOOL_OUT_B = '<\uFF5Ctool\u2581outputs\u2581begin\uFF5C><\uFF5Ctool\u2581output\u2581begin\uFF5C>';
const TOOL_OUT_E = '<\uFF5Ctool\u2581output\u2581end\uFF5C><\uFF5Ctool\u2581outputs\u2581end\uFF5C>';

function safeArgs(args) {
  const a = (args == null ? '' : String(args)).trim();
  if (!a) return '{}';
  try { JSON.parse(a); return a; } catch (e) { return JSON.stringify(a); }
}

async function contentToText(content, res, collectImages) {
  if (content == null) return '';
  if (typeof content === 'string') return content.trim();
  if (!Array.isArray(content)) return '';
  const parts = [];
  for (const p of content) {
    if (!p || typeof p !== 'object') continue;
    const type = p.type;
    if (type === 'text' || type === 'input_text') {
      parts.push(p.text || '');
    } else if (type === 'image_url') {
      const url = (p.image_url && p.image_url.url) || p.image_url || '';
      const a = await toAttachment(url, 'image');
      if (a) { res.attachments.push(a); if (collectImages) parts.push(`[已附加图片 ${a.name}]`); }
    } else if (type === 'input_image') {
      const a = await toAttachment(p.image_url || '', 'image');
      if (a) { res.attachments.push(a); if (collectImages) parts.push(`[已附加图片 ${a.name}]`); }
    } else if (type === 'file' || type === 'input_file') {
      const f = p.file || p;
      const name = f.filename || f.name || 'file';
      const data = f.file_data || f.data || '';
      if (data.startsWith('data:')) {
        const du = parseDataUrl(data);
        if (du) { res.attachments.push({ name, mime: du.mime, base64: du.b64 }); parts.push(`[已附加文件 ${name}]`); }
      } else if (/^https?:/.test(data)) {
        const a = await fetchAttachment(data, name);
        if (a) { res.attachments.push(a); parts.push(`[已附加文件 ${name}]`); }
      }
    }
  }
  return parts.join('\n').trim();
}

function parseDataUrl(s) {
  try {
    const comma = s.indexOf(',');
    if (comma < 0) return null;
    const head = s.slice(5, comma);          // 去掉 "data:"
    let mime = head;
    const semi = head.indexOf(';');
    if (semi >= 0) mime = head.slice(0, semi);
    if (!mime) mime = 'application/octet-stream';
    return { mime, b64: s.slice(comma + 1).trim() };
  } catch (e) { return null; }
}

function extFor(mime, url) {
  if (!mime) mime = '';
  if (mime.includes('png')) return '.png';
  if (mime.includes('jpeg') || mime.includes('jpg')) return '.jpg';
  if (mime.includes('gif')) return '.gif';
  if (mime.includes('webp')) return '.webp';
  if (mime.includes('pdf')) return '.pdf';
  if (mime.includes('text')) return '.txt';
  const q = url.indexOf('?');
  const p = q >= 0 ? url.slice(0, q) : url;
  const dot = p.lastIndexOf('.');
  const slash = p.lastIndexOf('/');
  if (dot > slash && dot >= 0) return p.slice(dot);
  return '';
}

async function fetchAttachment(url, nameHint) {
  try {
    const resp = await fetch(url, { redirect: 'follow' });
    if (!resp.ok) return null;
    let mime = resp.headers.get('content-type') || 'application/octet-stream';
    const semi = mime.indexOf(';');
    if (semi >= 0) mime = mime.slice(0, semi).trim();
    const buf = Buffer.from(await resp.arrayBuffer());
    if (!buf.length || buf.length > 25 * 1024 * 1024) return null;
    let name = nameHint || ('file-' + Date.now());
    if (!name.includes('.')) name += extFor(mime, url);
    return { name, mime, base64: buf.toString('base64') };
  } catch (e) {
    return null;
  }
}

async function toAttachment(url, kind) {
  if (!url) return null;
  if (url.startsWith('data:')) {
    const du = parseDataUrl(url);
    if (!du) return null;
    const ext = extFor(du.mime, '');
    return { name: `${kind}-${Date.now()}${ext}`, mime: du.mime, base64: du.b64 };
  }
  if (/^https?:/.test(url)) return fetchAttachment(url, `${kind}-${Date.now()}`);
  return null;
}

function appendAssistant(m, body, res) {
  return contentToText(m.content, res, false).then((t) => {
    const calls = Array.isArray(m.tool_calls) ? m.tool_calls : [];
    let s = t || '';
    if (calls.length) {
      const items = calls.map((tc) => {
        const fn = (tc && tc.function) || {};
        return `{"name": ${JSON.stringify(fn.name || '')}, "arguments": ${safeArgs(fn.arguments)}}`;
      });
      s += TOOL_START + '[' + items.join(', ') + ']' + TOOL_END;
    }
    if (s) { if (body.length) body.push(''); body.push(s); }
  });
}

function buildToolBlock(tools, req) {
  const out = [];
  const names = [];
  out.push('你可以使用以下工具：');
  for (const t of tools) {
    const fn = t && t.function;
    if (!fn || !fn.name) continue;
    names.push(fn.name);
    const params = JSON.stringify(fn.parameters || {});
    const desc = (fn.description || '').trim();
    out.push(`- **${fn.name}** (function):`);
    out.push(`  - 调用方法: \`${TOOL_START}[{"name": "${fn.name}", "arguments": ${params}}]${TOOL_END}\``);
    out.push(`  - 简要说明:\n~~~markdown\n  ${desc || '无描述'}\n~~~`);
  }
  out.push('');
  out.push('**工具调用格式 — 请严格遵守：**');
  out.push('');
  out.push('将 JSON 数组包裹在工具调用标记中：');
  out.push('');
  out.push(`${TOOL_START}[{"name": "工具名", "arguments": {参数JSON}}]${TOOL_END}`);
  out.push('');
  out.push('**规则：**');
  out.push('');
  out.push('1. 决定调用工具时，响应中**只允许**出现工具调用文本本身，禁止任何解释、前缀、总结、问候语。');
  out.push(`2. JSON 数组必须以 \`${TOOL_START}\` 开头、以 \`${TOOL_END}\` 结尾，完整包裹。`);
  out.push('3. 所有工具调用放在**一个** JSON 数组中，多个用逗号分隔。');
  out.push(`4. 输出 \`${TOOL_END}\` 后立即停止，不要添加后续文字。`);
  out.push('5. 不要用 markdown 代码块包裹工具调用。');
  out.push('6. 字符串参数值用**双引号**（标准 JSON）。');
  out.push('7. 不要将工具调用或最终回复放进思考内容里。');
  if (names.length) {
    out.push('');
    out.push('**示例**（调用一个工具）：');
    out.push(`${TOOL_START}[{"name": "${names[0]}", "arguments": {}}]${TOOL_END}`);
    if (names.length >= 2) {
      out.push('');
      out.push('**示例**（并行调用两个工具）：');
      out.push(`${TOOL_START}[{"name": "${names[0]}", "arguments": {}}, {"name": "${names[1]}", "arguments": {}}]${TOOL_END}`);
    }
  }
  if (req.tool_choice === 'required') { out.push(''); out.push('**注意：你必须调用一个或多个工具。**'); }
  return out.join('\n');
}

function responseFormatBlock(rf) {
  if (!rf) return '';
  if (rf.type === 'json_object') return '请直接输出合法的 JSON 对象，不要包含 markdown 代码块标记或解释性文字。';
  if (rf.type === 'json_schema') return '请以 JSON 形式输出，并遵守以下 JSON Schema：\n' + JSON.stringify(rf.json_schema || {});
  return '';
}

/** 主入口：返回 { text, attachments, hasTools } */
async function build(req, stateless) {
  const res = { text: '', attachments: [], hasTools: false };
  const messages = Array.isArray(req.messages) ? req.messages : [];
  if (!messages.length) return res;

  const tools = Array.isArray(req.tools) ? req.tools : [];
  res.hasTools = tools.length > 0;
  const toolBlock = res.hasTools ? buildToolBlock(tools, req) : '';
  const fmtBlock = responseFormatBlock(req.response_format);

  if (stateless) {
    res.text = await buildStateless(messages, toolBlock, fmtBlock, res);
    return res;
  }

  // 会话模式：delta = 最后一条 assistant 之后的消息
  let deltaStart = 0;
  for (let i = messages.length - 1; i >= 0; i--) {
    if (messages[i] && messages[i].role === 'assistant') { deltaStart = i + 1; break; }
  }

  const sysBuf = [];
  const body = [];
  for (let i = deltaStart; i < messages.length; i++) {
    const m = messages[i];
    if (!m) continue;
    if (m.role === 'system') {
      const t = await contentToText(m.content, res, false);
      if (t) sysBuf.push(t);
    } else if (m.role === 'assistant') {
      await appendAssistant(m, body, res);
    } else if (m.role === 'tool' || m.role === 'function') {
      const t = await contentToText(m.content, res, false);
      if (body.length) body.push('');
      body.push(TOOL_OUT_B + t + TOOL_OUT_E);
    } else {
      const t = await contentToText(m.content, res, true);
      if (t) { if (body.length) body.push(''); body.push(t); }
    }
  }

  const head = [];
  if (sysBuf.length || toolBlock || fmtBlock) {
    head.push('\u3010系统指令\u3011');
    if (sysBuf.length) head.push(sysBuf.join('\n\n'));
    if (toolBlock) head.push(toolBlock);
    if (fmtBlock) head.push(fmtBlock);
    head.push('');
  }
  res.text = head.concat(body).join('\n').trim();
  return res;
}

async function buildStateless(messages, toolBlock, fmtBlock, res) {
  const sysBuf = [];
  for (const m of messages) {
    if (m && m.role === 'system') {
      const t = await contentToText(m.content, res, false);
      if (t) sysBuf.push(t);
    }
  }
  const body = [];
  if (sysBuf.length) body.push('\u3010系统指令\u3011\n' + sysBuf.join('\n\n'));
  if (toolBlock) body.push(toolBlock);
  if (fmtBlock) body.push(fmtBlock);
  if (body.length) body.push('');
  for (const m of messages) {
    if (!m || m.role === 'system') continue;
    if (m.role === 'assistant') { await appendAssistant(m, body, res); }
    else if (m.role === 'tool' || m.role === 'function') {
      const t = await contentToText(m.content, res, false);
      if (body.length) body.push('');
      body.push(TOOL_OUT_B + t + TOOL_OUT_E);
    } else {
      const t = await contentToText(m.content, res, true);
      if (t) { if (body.length) body.push(''); body.push(t); }
    }
  }
  return body.join('\n').trim();
}

module.exports = { build, TOOL_START, TOOL_END, TOOL_OUT_B, TOOL_OUT_E };
