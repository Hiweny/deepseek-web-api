'use strict';
/** OpenAI 兼容协议转换 + 工具调用解析（移植自 Android 版 OpenAiAdapter）。 */
const crypto = require('crypto');

const START = '<|tool\u2581calls\u2581begin|>';
const END = '<|tool\u2581calls\u2581end|>';
const START_NORM = '<|tool_calls_begin|>';
const END_NORM = '<|tool_calls_end|>';

function normTag(s) {
  if (!s) return '';
  return String(s).replace(/\uFF5C/g, '|').replace(/\u2581/g, '_');
}

function tryParseArray(s) { try { const v = JSON.parse(s); return Array.isArray(v) ? v : null; } catch (e) { return null; } }
function tryParseObject(s) { try { const v = JSON.parse(s); return (v && typeof v === 'object' && !Array.isArray(v)) ? v : null; } catch (e) { return null; } }

function normalizeCalls(arr) {
  const out = [];
  for (const item of arr) {
    if (!item || typeof item !== 'object') continue;
    const name = item.name || item.function || '';
    let args = item.arguments != null ? item.arguments : (item.parameters != null ? item.parameters : null);
    const call = { name };
    if (args && typeof args === 'object') call.arguments = args;
    else if (typeof args === 'string') {
      const t = args.trim();
      const o = tryParseObject(t);
      if (o) call.arguments = o;
      else { call.arguments = {}; call._raw_args = t; }
    } else call.arguments = {};
    out.push(call);
  }
  return out;
}

/** 扫描顶层 {...} 片段（忽略字符串内括号）。 */
function extractObjects(s) {
  const out = [];
  let depth = 0, start = -1, inStr = false, q = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (inStr) {
      if (c === '\\') { i++; continue; }
      if (c === q) inStr = false;
      continue;
    }
    if (c === '"' || c === "'") { inStr = true; q = c; continue; }
    if (c === '{') { if (depth === 0) start = i; depth++; }
    else if (c === '}') { depth--; if (depth === 0 && start >= 0) { out.push(s.slice(start, i + 1)); start = -1; } }
  }
  return out;
}

function parseToolCalls(inner) {
  if (inner == null) return null;
  let s = String(inner).trim();
  s = s.replace(/^```[a-zA-Z0-9]*\s*/, '').replace(/```\s*$/, '').trim();
  const lb = s.indexOf('['), rb = s.lastIndexOf(']');
  if (lb >= 0 && rb > lb) {
    const a = tryParseArray(s.slice(lb, rb + 1));
    if (a) return normalizeCalls(a);
  }
  const a2 = tryParseArray(s);
  if (a2) return normalizeCalls(a2);
  const objs = extractObjects(s);
  if (objs.length) {
    const calls = [];
    for (const o of objs) { const j = tryParseObject(o); if (j) calls.push(j); }
    if (calls.length) return normalizeCalls(calls);
  }
  const one = tryParseObject(s);
  if (one) return normalizeCalls([one]);
  return null;
}

/** 从模型输出解析工具调用；返回 {content, thinking, toolCalls, finishReason}。 */
function process(thinkingText, contentText) {
  const r = { content: '', thinking: thinkingText || '', toolCalls: null, finishReason: 'stop' };
  const content = contentText || '';
  const norm = normTag(content);
  const s = norm.indexOf(START_NORM);
  if (s < 0) { r.content = content; return r; }
  const e = norm.indexOf(END_NORM, s + START_NORM.length);
  let inner, after;
  if (e < 0) { inner = content.slice(s + START_NORM.length); after = content.length; }
  else { inner = content.slice(s + START_NORM.length, e); after = e + END_NORM.length; }
  const calls = parseToolCalls(inner);
  if (!calls || !calls.length) { r.content = content; return r; }
  r.toolCalls = calls;
  r.finishReason = 'tool_calls';
  const before = content.slice(0, s).trim();
  const rest = after < content.length ? content.slice(after).trim() : '';
  r.content = (before + (rest ? '\n' + rest : '')).trim();
  return r;
}

function newId() { return 'chatcmpl-' + crypto.randomBytes(12).toString('hex'); }
function newCallId() { return 'call_' + crypto.randomBytes(11).toString('hex'); }

function estTokens(s) {
  if (!s) return 0;
  return Math.max(1, Math.ceil(String(s).length / 2));
}

function toOpenAiToolCalls(calls) {
  return calls.map((c) => {
    const args = c.arguments == null ? '{}' : (typeof c.arguments === 'string' ? c.arguments : JSON.stringify(c.arguments));
    return { id: newCallId(), type: 'function', function: { name: c.name || '', arguments: args } };
  });
}

function buildMessage(thinking, content, toolCalls) {
  const msg = { role: 'assistant' };
  if (toolCalls && toolCalls.length) {
    msg.content = content ? content : null;
    msg.tool_calls = toOpenAiToolCalls(toolCalls);
  } else {
    msg.content = content == null ? '' : content;
  }
  if (thinking) msg.reasoning_content = thinking;
  return msg;
}

function buildCompletion(id, model, created, thinking, content, toolCalls, finishReason, promptTokens, completionTokens) {
  return {
    id, object: 'chat.completion', created, model,
    choices: [{ index: 0, message: buildMessage(thinking, content, toolCalls), finish_reason: finishReason }],
    usage: { prompt_tokens: promptTokens, completion_tokens: completionTokens, total_tokens: promptTokens + completionTokens },
  };
}

/** 生成一条 SSE chunk 对象（调用方负责序列化并加 "data: "）。 */
function chunk(id, model, created, delta, finishReason) {
  return {
    id, object: 'chat.completion.chunk', created, model,
    choices: [{ index: 0, delta, finish_reason: finishReason == null ? null : finishReason }],
  };
}

function modelsList() {
  return {
    object: 'list',
    data: [
      { id: 'deepseek', object: 'model', created: 1700000000, owned_by: 'deepseek-web' },
      { id: 'deepseek-chat', object: 'model', created: 1700000000, owned_by: 'deepseek-web' },
      { id: 'deepseek-reasoner', object: 'model', created: 1700000000, owned_by: 'deepseek-web' },
    ],
  };
}

module.exports = {
  START, END, normTag, process, parseToolCalls, newId, estTokens,
  toOpenAiToolCalls, buildMessage, buildCompletion, chunk, modelsList,
};
