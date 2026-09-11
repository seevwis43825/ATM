/**
 * API 服务入口 —— 负责人：B
 *
 * 前端只需要调三个接口：
 *   POST /api/chat     发送消息，拿回复 + UI 指令
 *   POST /api/confirm  用户确认 / 拒绝敏感操作
 *   GET  /api/health   健康检查（部署材料需要）
 */

import express from 'express';
import cors from 'cors';
import path from 'node:path';
import fs from 'node:fs';
import { handleChat, handleConfirm } from './agent';
import { listAudit } from './lib/store';

const app = express();
const PORT = Number(process.env.API_PORT ?? 3001);
const MOCK_MODE = process.env.MOCK_MODE !== 'false';

app.use(cors());
app.use(express.json({ limit: '1mb' }));

// ---------- 请求日志（演示时方便排查）----------
app.use((req, _res, next) => {
  if (req.path.startsWith('/api')) {
    console.log(`[${new Date().toISOString()}] ${req.method} ${req.path}`);
  }
  next();
});

// ---------- 健康检查 ----------
app.get('/api/health', (_req, res) => {
  res.json({
    status: 'ok',
    version: '1.0.0',
    mockMode: MOCK_MODE,
    time: new Date().toISOString(),
  });
});

// ---------- 主对话接口 ----------
app.post('/api/chat', async (req, res) => {
  try {
    const { sessionId, userId, message } = req.body ?? {};

    if (!message || typeof message !== 'string' || !message.trim()) {
      return res.status(400).json({
        error: 'BAD_REQUEST',
        message: 'message 不能为空',
      });
    }

    const sid = sessionId || `sess_${Date.now()}`;
    const uid = userId || 'u_1001'; // 演示默认用户

    const result = await handleChat(sid, uid, message.trim());
    res.json(result);
  } catch (err) {
    console.error('[chat] 处理失败：', err);
    res.status(500).json({
      error: 'INTERNAL_ERROR',
      message: err instanceof Error ? err.message : '服务器内部错误',
    });
  }
});

// ---------- 确认 / 拒绝 ----------
app.post('/api/confirm', async (req, res) => {
  try {
    const { sessionId, auditId, action } = req.body ?? {};

    if (!auditId || !action) {
      return res.status(400).json({
        error: 'BAD_REQUEST',
        message: 'auditId 和 action 必填',
      });
    }
    if (!['approve', 'reject'].includes(action)) {
      return res.status(400).json({
        error: 'BAD_REQUEST',
        message: "action 只能是 'approve' 或 'reject'",
      });
    }

    const result = await handleConfirm({
      sessionId: sessionId || 'unknown',
      auditId,
      action,
    });
    res.json(result);
  } catch (err) {
    console.error('[confirm] 处理失败：', err);
    res.status(500).json({
      error: 'INTERNAL_ERROR',
      message: err instanceof Error ? err.message : '服务器内部错误',
    });
  }
});

// ---------- 审计日志（安全自评报告的数据来源）----------
app.get('/api/audit', (req, res) => {
  const userId = typeof req.query.userId === 'string' ? req.query.userId : undefined;
  const logs = listAudit(userId);
  res.json({ count: logs.length, logs: logs.slice(-50) });
});

// ---------- 前端静态文件（生产构建后）----------
const webDist = path.resolve(__dirname, '../../../web/dist');
if (fs.existsSync(webDist)) {
  app.use(express.static(webDist));
  app.get(/^(?!\/api).*/, (_req, res) => {
    res.sendFile(path.join(webDist, 'index.html'));
  });
}

// ---------- 启动 ----------
const server = app.listen(PORT, () => {
  console.log('');
  console.log('  AI Banking Agent API');
  console.log('  ────────────────────────────────────');
  console.log(`  服务地址   http://localhost:${PORT}`);
  console.log(`  运行模式   ${MOCK_MODE ? 'MOCK（无需 AI key）' : 'LIVE'}`);
  console.log(`  健康检查   http://localhost:${PORT}/api/health`);
  console.log('  ────────────────────────────────────');
  console.log('');
});

process.on('SIGINT', () => {
  console.log('\n正在关闭服务...');
  server.close(() => process.exit(0));
});

export default app;
