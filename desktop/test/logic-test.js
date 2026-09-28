'use strict';
/**
 * 纯 Node 逻辑自测：用 mock 通道模拟官网页面，验证
 * prompt 构建 / 工具调用解析 / 非流式与流式 API 全链路。
 * 运行：node test/logic-test.js
 */
const os = require('os');
const path = require('path');
const fs = require('fs');

const settings = require('../src/settings');
const logger = require('../src/log');
const BridgeController = require('../src/bridge-controller');
const ApiServer = require('../src/server');
const adapter = require('../src/adapter');
const promptBuilder = require('../src/prompt');

let pass = 0, fail = 0;
function ok(cond, name, extra) {
  if (cond) { pass++; console.log('  PASS ' + name); }
  else { fail++; console.log('  FAIL ' + name + (extra ? ' -> ' + extra : '')); }
}

/* ---------- mock 页面通道 ---------- */
function makeTransport(controller, replyFn) {
  return {
    ready: () => true,
    inject: () => {},
    eval: (code) => {
      const m = code.match(/DSKB\.(\w+)\(/);
      if (!m) return;
      const method = m[1];
      const rid = (code.match(/"reqId":"([0-9a-f]+)"/) || [])[1] || '';
      setTimeout(() => {
        if (method === 'send') {
          const steps = replyFn();
          for (const st of steps) controller.onJsEvent(JSON.stringify(Object.assign({ reqId: rid }, st)));
        } else if (method === 'attachFile') {
          controller.onJsEvent(JSON.stringify({ type: 'attach', reqId: rid, ok: true, name: 'x.png' }));
        } else if (method === 'newChat') {
          controller.onJsEvent(JSON.stringify({ type: 'newChat', reqId: rid, ok: true }));
        }
      }, 5);
    },
  };
}

const PLAIN_REPLY = () => ([
  { type: 'delta', thinking: '思考中', content: '你好' },
  { type: 'delta', thinking: '思考中…完成', content: '你好，我是 DeepSeek 助手。' },
  { type: 'reply', ok: true, content: '你好，我是 DeepSeek 助手。', thinking: '思考中…完成', recalled: false },
]);
const TOOL_REPLY = () => ([
  { type: 'delta', thinking: '', content: '我来查一下天气。' },
  { type: 'delta', thinking: '', content: '我来查一下天气。<|tool\u2581calls\u2581begin|>[{"name": "get_weather", "arguments": {"city": "北京"}}]<|tool\u2581calls\u2581end|>' },
  { type: 'reply', ok: true, content: '我来查一下天气。<|tool\u2581calls\u2581begin|>[{"name": "get_weather", "arguments": {"city": "北京"}}]<|tool\u2581calls\u2581end|>', thinking: '', recalled: false },
]);
const RECALL_REPLY = () => ([
  { type: 'reply', ok: true, content: '（这是一条被撤回的回复真实内容）', thinking: '', recalled: true },
]);

/* ---------- 单元：adapter ---------- */
async function unitTests() {
  console.log('\n[unit] adapter.process');
  const r1 = adapter.process('思考', '正文内容');
  ok(r1.content === '正文内容' && r1.thinking === '思考' && r1.finishReason === 'stop', '普通回复');
  const r2 = adapter.process('', '前缀<|tool\u2581calls\u2581begin|>[{"name":"f","arguments":{"a":1}}]<|tool\u2581calls\u2581end|>后缀');
  ok(r2.finishReason === 'tool_calls' && r2.toolCalls && r2.toolCalls[0].name === 'f' && r2.toolCalls[0].arguments.a === 1, '工具调用解析');
  const r3 = adapter.process('', '<|tool_calls_begin|>[{"name":"g","arguments":"{\\"b\\":2}"}]<|tool_calls_end|>');
  ok(r3.toolCalls && r3.toolCalls[0].name === 'g' && r3.toolCalls[0].arguments.b === 2, 'ASCII 标签 + 字符串参数');
  const r4 = adapter.process('', '<|tool_calls_begin|>[{"name":"h","arguments":{"x":1}},{"name":"i","arguments":{}}]<|tool_calls_end|>');
  ok(r4.toolCalls && r4.toolCalls.length === 2, '并行多工具');

  console.log('\n[unit] prompt.build');
  const pb = await promptBuilder.build({
    messages: [{ role: 'system', content: '你是助手' }, { role: 'user', content: '天气如何' }],
    tools: [{ type: 'function', function: { name: 'get_weather', description: '查天气', parameters: { type: 'object' } } }],
  }, false);
  ok(pb.text.includes('你是助手') && pb.text.includes('天气如何') && pb.text.includes('get_weather') && pb.text.includes('<|tool\u2581calls\u2581begin|>'), '系统+工具注入');

  const pb2 = await promptBuilder.build({
    messages: [{ role: 'user', content: 'u1' }, { role: 'assistant', content: 'a1' }, { role: 'user', content: 'u2' }],
  }, false);
  ok(!pb2.text.includes('u1') && pb2.text.includes('u2'), '会话模式只发 delta');

  const pb3 = await promptBuilder.build({
    messages: [{ role: 'user', content: '北京天气' }, { role: 'assistant', tool_calls: [{ function: { name: 'get_weather', arguments: '{"city":"北京"}' } }] }, { role: 'tool', content: '晴 25℃' }],
  }, false);
  ok(pb3.text.includes('tool\u2581outputs\u2581begin') && pb3.text.includes('晴 25℃'), '工具结果注入');
}

/* ---------- 集成：HTTP API ---------- */
async function httpTests() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'dswebapi-'));
  settings.init(tmp);
  settings.update({ port: 18899, bindLan: false, apiKey: 'sk-test', timeout: 20 });

  const controller = new BridgeController((m) => {});
  const server = new ApiServer({ controller, settings, log: (m) => {} });
  server.onStats = () => {};
  server.start();
  await new Promise((r) => setTimeout(r, 300));

  const base = 'http://127.0.0.1:18899';
  const H = { 'Content-Type': 'application/json', 'Authorization': 'Bearer sk-test' };

  console.log('\n[api] auth');
  {
    const r = await fetch(base + '/v1/models', { headers: { Authorization: 'Bearer wrong' } });
    ok(r.status === 401, '错误 key 返回 401');
    const r2 = await fetch(base + '/v1/models', { headers: H });
    const j = await r2.json();
    ok(r2.status === 200 && j.data.some((m) => m.id === 'deepseek'), '模型列表含 deepseek');
  }

  console.log('\n[api] 非流式');
  {
    controller.attach(makeTransport(controller, PLAIN_REPLY));
    const r = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: H, body: JSON.stringify({ model: 'deepseek', messages: [{ role: 'user', content: '你好' }] }) });
    const j = await r.json();
    ok(r.status === 200, 'HTTP 200', r.status);
    ok(j.choices[0].message.content === '你好，我是 DeepSeek 助手。', '正文正确', j.choices && j.choices[0].message.content);
    ok(j.choices[0].message.reasoning_content === '思考中…完成', '思考分离到 reasoning_content');
    ok(j.choices[0].finish_reason === 'stop', 'finish_reason=stop');
  }

  console.log('\n[api] 流式');
  {
    const r = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: H, body: JSON.stringify({ model: 'deepseek', messages: [{ role: 'user', content: '你好' }], stream: true }) });
    const text = await r.text();
    ok(text.includes('"role":"assistant"'), 'SSE 首帧 role');
    ok(text.includes('"reasoning_content":"思考中'), 'SSE 思考增量');
    ok(text.includes('你好，我是 DeepSeek 助手。') || text.includes('你好'), 'SSE 正文增量');
    ok(text.trim().endsWith('data: [DONE]'), 'SSE 以 [DONE] 收尾');
    ok(text.includes('"finish_reason":"stop"'), 'SSE finish_reason');
  }

  console.log('\n[api] 工具调用（流式）');
  {
    controller.attach(makeTransport(controller, TOOL_REPLY));
    const r = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: H, body: JSON.stringify({ model: 'deepseek', messages: [{ role: 'user', content: '北京天气' }], stream: true, tools: [{ type: 'function', function: { name: 'get_weather', parameters: {} } }] }) });
    const text = await r.text();
    ok(text.includes('"tool_calls"'), 'SSE 含 tool_calls');
    ok(text.includes('get_weather'), 'SSE 含工具名');
    ok(!text.includes('tool_calls_begin'), '未把工具标签泄漏进正文', text.slice(0, 200));
    ok(text.includes('"finish_reason":"tool_calls"'), 'finish_reason=tool_calls');
    ok(text.includes('我来查一下天气。'), '标签前的正文已下发');
    const nonStream = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: H, body: JSON.stringify({ model: 'deepseek', messages: [{ role: 'user', content: '北京天气' }], tools: [{ type: 'function', function: { name: 'get_weather', parameters: {} } }] }) });
    const nj = await nonStream.json();
    ok(nj.choices[0].message.tool_calls && nj.choices[0].message.tool_calls[0].function.name === 'get_weather', '非流式 tool_calls');
    ok(nj.choices[0].finish_reason === 'tool_calls', '非流式 finish_reason=tool_calls');
  }

  console.log('\n[api] 防撤回');
  {
    controller.attach(makeTransport(controller, RECALL_REPLY));
    const r = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: H, body: JSON.stringify({ model: 'deepseek', messages: [{ role: 'user', content: '写点东西' }] }) });
    const j = await r.json();
    ok(j.choices[0].message.content.includes('被撤回'), '撤回内容仍返回');
  }

  console.log('\n[api] 附件');
  {
    controller.attach(makeTransport(controller, PLAIN_REPLY));
    const r = await fetch(base + '/v1/chat/completions', { method: 'POST', headers: H, body: JSON.stringify({ model: 'deepseek', messages: [{ role: 'user', content: [{ type: 'text', text: '看图' }, { type: 'image_url', image_url: { url: 'data:image/png;base64,iVBORw0KGgo=' } }] }] }) });
    const j = await r.json();
    ok(r.status === 200 && j.choices, '含图片 data URL 的请求成功');
  }

  console.log('\n[api] health');
  {
    const r = await fetch(base + '/health');
    const j = await r.json();
    ok(j.status === 'ok' && j.bridge_attached === true, '健康检查');
  }

  server.stop();
}

(async () => {
  try {
    await unitTests();
    await httpTests();
  } catch (e) {
    fail++;
    console.log('EXCEPTION: ' + (e && e.stack || e));
  }
  console.log(`\n===== 通过 ${pass} / 失败 ${fail} =====`);
  process.exit(fail ? 1 : 0);
})();
