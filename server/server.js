/**
 * Robotic Arm Simulator — REST + WebSocket + MCP Server
 * Run: npm install && node server.js
 * 
 * REST  → http://localhost:3000/api/arm/*
 * WS    → ws://localhost:3000/ws
 * MCP   → http://localhost:3000/mcp  (SSE transport)
 */

const express    = require('express');
const http       = require('http');
const WebSocket  = require('ws');
const cors       = require('cors');
const { v4: uuid } = require('uuid');

const app    = express();
const server = http.createServer(app);
const wss    = new WebSocket.Server({ server, path: '/ws' });

app.use(cors());
app.use(express.json());

// ─── Arm State ────────────────────────────────────────────────────────────────

const JOINT_DEFS = [
  { name: 'Base',      axis: 'y', min: -180, max:  180 },
  { name: 'Shoulder',  axis: 'z', min: -120, max:   80 },
  { name: 'Elbow',     axis: 'z', min: -135, max:  135 },
  { name: 'WristPitch',axis: 'z', min:  -90, max:   90 },
  { name: 'WristRoll', axis: 'y', min: -180, max:  180 },
  { name: 'Gripper',   axis: 'z', min:    0, max:   80 },
];

const HOME_POSITION = [0, 40, -90, 50, 0, 20];

const armState = {
  joints:      [...HOME_POSITION],    // degrees
  isMoving:    false,
  mode:        'manual',              // manual | auto | hold
  toolAttached:'gripper',
  timestamp:   Date.now(),
};

// Simulated sensor noise
function noise(scale = 0.01) { return (Math.random() - 0.5) * 2 * scale; }

function getSensors() {
  const j = armState.joints;
  return {
    joints: j.map((a, i) => ({
      id:       i,
      name:     JOINT_DEFS[i].name,
      angle_deg: parseFloat((a + noise(0.02)).toFixed(4)),
      torque_nm: parseFloat((Math.abs(a) * 0.012 + noise(0.05) + 0.03).toFixed(4)),
      velocity_rads: parseFloat((noise(0.008)).toFixed(6)),
      temp_c:   parseFloat((28 + Math.abs(a) * 0.04 + noise(0.2)).toFixed(2)),
    })),
    endEffector: forwardKinematics(j),
    gripper: {
      aperture_deg: j[5],
      force_n: parseFloat((noise(0.1) + 0.05).toFixed(4)),
      contact: j[5] < 5,
    },
    power: {
      voltage_v: parseFloat((24.0 + noise(0.1)).toFixed(3)),
      current_a: parseFloat((j.reduce((s, a) => s + Math.abs(a) * 0.001, 0.2) + noise(0.02)).toFixed(3)),
    },
    timestamp: Date.now(),
  };
}

function forwardKinematics(angles) {
  const R = Math.PI / 180;
  const [L1, L2, L3] = [1.1, 0.9, 0.52];
  const r  = angles[0] * R;
  const t1 = angles[1] * R;
  const t2 = t1 + angles[2] * R;
  const t3 = t2 + angles[3] * R;
  const reach = L1 * Math.cos(t1) + L2 * Math.cos(t2) + L3 * Math.cos(t3);
  return {
    x: parseFloat((reach * Math.sin(r)).toFixed(4)),
    y: parseFloat((0.82 + L1 * Math.sin(t1) + L2 * Math.sin(t2) + L3 * Math.sin(t3)).toFixed(4)),
    z: parseFloat((reach * Math.cos(r)).toFixed(4)),
    yaw_deg:   parseFloat((angles[0]).toFixed(2)),
    pitch_deg: parseFloat(((angles[1] + angles[2] + angles[3])).toFixed(2)),
    roll_deg:  parseFloat((angles[4]).toFixed(2)),
  };
}

function clampAngle(value, jointId) {
  const def = JOINT_DEFS[jointId];
  return Math.max(def.min, Math.min(def.max, value));
}

function setJoints(angles) {
  armState.joints = angles.map((a, i) => clampAngle(a, i));
  armState.timestamp = Date.now();
  broadcastState();
}

// ─── WebSocket broadcast ──────────────────────────────────────────────────────

function broadcastState(type = 'state') {
  const payload = JSON.stringify({
    type,
    data: {
      joints:      armState.joints,
      mode:        armState.mode,
      isMoving:    armState.isMoving,
      timestamp:   armState.timestamp,
    },
  });
  wss.clients.forEach(c => { if (c.readyState === WebSocket.OPEN) c.send(payload); });
}

wss.on('connection', (ws) => {
  console.log('[WS] Client connected');
  // Send current state immediately on connect
  ws.send(JSON.stringify({ type: 'welcome', data: { joints: armState.joints, mode: armState.mode } }));

  ws.on('message', (raw) => {
    try {
      const msg = JSON.parse(raw);
      if (msg.type === 'set_joint') {
        armState.joints[msg.id] = clampAngle(msg.angle, msg.id);
        armState.timestamp = Date.now();
        broadcastState();
      } else if (msg.type === 'set_joints') {
        setJoints(msg.angles);
      } else if (msg.type === 'home') {
        setJoints([...HOME_POSITION]);
      } else if (msg.type === 'get_sensors') {
        ws.send(JSON.stringify({ type: 'sensors', data: getSensors() }));
      }
    } catch (e) {
      ws.send(JSON.stringify({ type: 'error', message: e.message }));
    }
  });

  ws.on('close', () => console.log('[WS] Client disconnected'));
});

// ─── REST API ─────────────────────────────────────────────────────────────────

// GET  /api/arm/state
app.get('/api/arm/state', (req, res) => {
  res.json({
    joints:    armState.joints,
    jointDefs: JOINT_DEFS,
    mode:      armState.mode,
    isMoving:  armState.isMoving,
    endEffector: forwardKinematics(armState.joints),
    timestamp: armState.timestamp,
  });
});

// GET  /api/arm/sensors
app.get('/api/arm/sensors', (req, res) => {
  res.json(getSensors());
});

// POST /api/arm/joint/:id   { "angle": 45 }
app.post('/api/arm/joint/:id', (req, res) => {
  const id = parseInt(req.params.id);
  if (id < 0 || id >= 6) return res.status(400).json({ error: 'Joint id must be 0–5' });
  const angle = req.body?.angle;
  if (typeof angle !== 'number') return res.status(400).json({ error: 'angle (number) required' });
  armState.joints[id] = clampAngle(angle, id);
  armState.timestamp = Date.now();
  broadcastState();
  res.json({ ok: true, joint: id, angle: armState.joints[id] });
});

// POST /api/arm/joints   { "angles": [0, 40, -90, 50, 0, 20] }
app.post('/api/arm/joints', (req, res) => {
  const angles = req.body?.angles;
  if (!Array.isArray(angles) || angles.length !== 6)
    return res.status(400).json({ error: 'angles must be array of 6 numbers' });
  setJoints(angles);
  res.json({ ok: true, joints: armState.joints });
});

// POST /api/arm/home
app.post('/api/arm/home', (req, res) => {
  setJoints([...HOME_POSITION]);
  res.json({ ok: true, joints: armState.joints });
});

// POST /api/arm/zero
app.post('/api/arm/zero', (req, res) => {
  setJoints(new Array(6).fill(0));
  res.json({ ok: true, joints: armState.joints });
});

// POST /api/arm/mode   { "mode": "auto" | "manual" | "hold" }
app.post('/api/arm/mode', (req, res) => {
  const mode = req.body?.mode;
  if (!['auto', 'manual', 'hold'].includes(mode))
    return res.status(400).json({ error: 'mode must be auto | manual | hold' });
  armState.mode = mode;
  broadcastState();
  res.json({ ok: true, mode: armState.mode });
});

// POST /api/arm/sequence   { "steps": [{ "joints": [...], "dwell_ms": 500 }, ...] }
app.post('/api/arm/sequence', async (req, res) => {
  const steps = req.body?.steps;
  if (!Array.isArray(steps)) return res.status(400).json({ error: 'steps[] required' });
  res.json({ ok: true, steps: steps.length, message: 'Sequence started' });
  // Execute async after response
  for (const step of steps) {
    setJoints(step.joints);
    await new Promise(r => setTimeout(r, step.dwell_ms ?? 500));
  }
  broadcastState('sequence_complete');
});

// GET  /api/arm/joints/defs
app.get('/api/arm/joints/defs', (req, res) => {
  res.json(JOINT_DEFS);
});

// ─── MCP Server (SSE transport) ───────────────────────────────────────────────
//
// Implements a minimal subset of the Model Context Protocol spec so an LLM
// (Claude, GPT-4, etc.) can control the arm directly as an MCP tool server.
//
// Clients send JSON-RPC 2.0 messages via POST /mcp/message
// and receive streamed events from GET /mcp/sse

const mcpSessions = new Map(); // sessionId → { res, events[] }

// MCP tool definitions
const MCP_TOOLS = [
  {
    name: 'arm_get_state',
    description: 'Get the current joint angles, end-effector position, and mode of the robotic arm.',
    inputSchema: { type: 'object', properties: {}, required: [] },
  },
  {
    name: 'arm_set_joint',
    description: 'Set a single joint angle by joint index (0=Base, 1=Shoulder, 2=Elbow, 3=WristPitch, 4=WristRoll, 5=Gripper).',
    inputSchema: {
      type: 'object',
      properties: {
        joint_id: { type: 'integer', minimum: 0, maximum: 5, description: 'Joint index 0–5' },
        angle:    { type: 'number', description: 'Target angle in degrees' },
      },
      required: ['joint_id', 'angle'],
    },
  },
  {
    name: 'arm_set_joints',
    description: 'Set all 6 joint angles at once. Provide array of 6 degree values.',
    inputSchema: {
      type: 'object',
      properties: {
        angles: { type: 'array', items: { type: 'number' }, minItems: 6, maxItems: 6 },
      },
      required: ['angles'],
    },
  },
  {
    name: 'arm_home',
    description: 'Move the arm to its home/rest position.',
    inputSchema: { type: 'object', properties: {}, required: [] },
  },
  {
    name: 'arm_get_sensors',
    description: 'Read all sensor data: per-joint torque, velocity, temperature, end-effector position, power.',
    inputSchema: { type: 'object', properties: {}, required: [] },
  },
  {
    name: 'arm_run_sequence',
    description: 'Execute a sequence of poses. Each step has joints[] array and optional dwell_ms.',
    inputSchema: {
      type: 'object',
      properties: {
        steps: {
          type: 'array',
          items: {
            type: 'object',
            properties: {
              joints:   { type: 'array', items: { type: 'number' }, minItems: 6, maxItems: 6 },
              dwell_ms: { type: 'integer', minimum: 0 },
            },
            required: ['joints'],
          },
        },
      },
      required: ['steps'],
    },
  },
];

function mcpDispatch(method, params) {
  switch (method) {
    case 'initialize':
      return {
        protocolVersion: '2024-11-05',
        capabilities: { tools: {} },
        serverInfo: { name: 'robotic-arm-sim', version: '1.0.0' },
      };
    case 'tools/list':
      return { tools: MCP_TOOLS };
    case 'tools/call': {
      const { name, arguments: args } = params;
      switch (name) {
        case 'arm_get_state':
          return { content: [{ type: 'text', text: JSON.stringify({
            joints: armState.joints, mode: armState.mode,
            endEffector: forwardKinematics(armState.joints),
          }, null, 2) }] };
        case 'arm_set_joint': {
          const id = args.joint_id;
          armState.joints[id] = clampAngle(args.angle, id);
          armState.timestamp = Date.now();
          broadcastState();
          return { content: [{ type: 'text', text: `Joint ${id} (${JOINT_DEFS[id].name}) set to ${armState.joints[id].toFixed(2)}°` }] };
        }
        case 'arm_set_joints':
          setJoints(args.angles);
          return { content: [{ type: 'text', text: `All joints set: ${armState.joints.map(a => a.toFixed(1)).join(', ')}` }] };
        case 'arm_home':
          setJoints([...HOME_POSITION]);
          return { content: [{ type: 'text', text: 'Arm moved to home position.' }] };
        case 'arm_get_sensors':
          return { content: [{ type: 'text', text: JSON.stringify(getSensors(), null, 2) }] };
        case 'arm_run_sequence': {
          const steps = args.steps;
          // Fire-and-forget
          (async () => {
            for (const step of steps) {
              setJoints(step.joints);
              await new Promise(r => setTimeout(r, step.dwell_ms ?? 500));
            }
            broadcastState('sequence_complete');
          })();
          return { content: [{ type: 'text', text: `Sequence of ${steps.length} steps started.` }] };
        }
        default:
          throw { code: -32601, message: `Unknown tool: ${name}` };
      }
    }
    default:
      throw { code: -32601, message: `Method not found: ${method}` };
  }
}

// SSE endpoint — client connects here first
app.get('/mcp/sse', (req, res) => {
  const sessionId = uuid();
  res.setHeader('Content-Type', 'text/event-stream');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'keep-alive');
  res.flushHeaders();

  mcpSessions.set(sessionId, res);
  // Send the endpoint URL so client knows where to POST messages
  res.write(`event: endpoint\ndata: ${JSON.stringify({ uri: `/mcp/message?sessionId=${sessionId}` })}\n\n`);

  req.on('close', () => mcpSessions.delete(sessionId));
});

// Message endpoint — client POSTs JSON-RPC here
app.post('/mcp/message', (req, res) => {
  const { sessionId } = req.query;
  const sseRes = mcpSessions.get(sessionId);
  const { id, method, params } = req.body;

  let result, error;
  try {
    result = mcpDispatch(method, params ?? {});
  } catch (e) {
    error = e;
  }

  const rpc = error
    ? { jsonrpc: '2.0', id, error }
    : { jsonrpc: '2.0', id, result };

  // Send back via SSE stream if session exists, otherwise HTTP response
  if (sseRes) {
    sseRes.write(`event: message\ndata: ${JSON.stringify(rpc)}\n\n`);
    res.sendStatus(202);
  } else {
    res.json(rpc);
  }
});

// Also support direct stateless POST to /mcp (easier for simple clients)
app.post('/mcp', (req, res) => {
  const { id, method, params } = req.body;
  try {
    res.json({ jsonrpc: '2.0', id, result: mcpDispatch(method, params ?? {}) });
  } catch (e) {
    res.json({ jsonrpc: '2.0', id, error: e });
  }
});

// ─── Start ────────────────────────────────────────────────────────────────────
const PORT = process.env.PORT || 3000;
server.listen(PORT, () => {
  console.log(`\n🦾  Robotic Arm Simulator`);
  console.log(`   REST  → http://localhost:${PORT}/api/arm/state`);
  console.log(`   WS    → ws://localhost:${PORT}/ws`);
  console.log(`   MCP   → http://localhost:${PORT}/mcp  (stateless)`);
  console.log(`   MCP   → http://localhost:${PORT}/mcp/sse  (SSE transport)\n`);
});
