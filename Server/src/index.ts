import { randomUUID } from "node:crypto";
import { EventEmitter } from "node:events";
import http from "node:http";

import express from "express";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import { WebSocket, WebSocketServer } from "ws";
import { z } from "zod";

const SERVER_NAME = "unity-mcp";
const SERVER_VERSION = "0.1.0";
const HOST = "127.0.0.1";
const DEFAULT_PORT = 48091;
const MCP_HTTP_PATH = "/mcp";
const UNITY_WS_PATH = "/unity";
const PROTOCOL_VERSION = 1;
const QUEUE_MAX_SIZE = 32;
const MAX_MESSAGE_BYTES = 1024 * 1024;
const HEARTBEAT_INTERVAL_MS = 3000;
const HEARTBEAT_TIMEOUT_MS = 4500;
const REQUEST_RECONNECT_WAIT_MS = 2500;

type ServerState = "booting" | "waiting_editor" | "ready" | "stopping" | "stopped";
type EditorState = "unknown" | "ready" | "compiling" | "reloading";
type JobState = "queued" | "running" | "succeeded" | "failed" | "timeout" | "cancelled";

type ToolName =
  | "get_editor_state"
  | "read_console"
  | "run_tests"
  | "get_job_status"
  | "cancel_job";

interface ToolMetadata {
  name: ToolName;
  execution_mode: "sync" | "job";
  supports_cancel: boolean;
  default_timeout_ms: number;
  max_timeout_ms: number;
  requires_client_request_id: boolean;
  execution_error_retryable: boolean;
}

const TOOL_CATALOG: ToolMetadata[] = [
  {
    name: "get_editor_state",
    execution_mode: "sync",
    supports_cancel: false,
    default_timeout_ms: 5000,
    max_timeout_ms: 10000,
    requires_client_request_id: false,
    execution_error_retryable: true,
  },
  {
    name: "read_console",
    execution_mode: "sync",
    supports_cancel: false,
    default_timeout_ms: 10000,
    max_timeout_ms: 30000,
    requires_client_request_id: false,
    execution_error_retryable: true,
  },
  {
    name: "run_tests",
    execution_mode: "job",
    supports_cancel: true,
    default_timeout_ms: 300000,
    max_timeout_ms: 1800000,
    requires_client_request_id: false,
    execution_error_retryable: false,
  },
  {
    name: "get_job_status",
    execution_mode: "sync",
    supports_cancel: false,
    default_timeout_ms: 5000,
    max_timeout_ms: 10000,
    requires_client_request_id: false,
    execution_error_retryable: false,
  },
  {
    name: "cancel_job",
    execution_mode: "sync",
    supports_cancel: false,
    default_timeout_ms: 5000,
    max_timeout_ms: 10000,
    requires_client_request_id: false,
    execution_error_retryable: false,
  },
];

class UnityMcpError extends Error {
  readonly code: string;
  readonly details?: unknown;

  constructor(code: string, message: string, details?: unknown) {
    super(message);
    this.name = "UnityMcpError";
    this.code = code;
    this.details = details;
  }
}

function log(level: "INFO" | "WARN" | "ERROR", message: string, context?: Record<string, unknown>): void {
  const payload = {
    level,
    ts: new Date().toISOString(),
    msg: message,
    ...(context ?? {}),
  };
  console.log(JSON.stringify(payload));
}

function toolTextPayload(payload: unknown): { content: Array<{ type: "text"; text: string }> } {
  return {
    content: [{ type: "text", text: JSON.stringify(payload, null, 2) }],
  };
}

function toolErrorPayload(error: unknown): { isError: true; content: Array<{ type: "text"; text: string }> } {
  if (error instanceof UnityMcpError) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: JSON.stringify(
            {
              code: error.code,
              message: error.message,
              details: error.details ?? {},
            },
            null,
            2,
          ),
        },
      ],
    };
  }

  return {
    isError: true,
    content: [
      {
        type: "text",
        text: JSON.stringify(
          {
            code: "ERR_UNITY_EXECUTION",
            message: error instanceof Error ? error.message : "Unexpected error",
          },
          null,
          2,
        ),
      },
    ],
  };
}

function parseConfig(argv: string[]): { port: number } {
  let port = DEFAULT_PORT;

  for (let i = 0; i < argv.length; i += 1) {
    const value = argv[i];

    if (value === "--port") {
      const next = argv[i + 1];
      if (!next) {
        throw new UnityMcpError("ERR_CONFIG_VALIDATION", "--port requires a value");
      }
      port = Number(next);
      i += 1;
      continue;
    }

    if (value.startsWith("--port=")) {
      port = Number(value.slice("--port=".length));
      continue;
    }
  }

  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new UnityMcpError(
      "ERR_CONFIG_VALIDATION",
      "--port must be an integer between 1 and 65535",
      { port },
    );
  }

  return { port };
}

function timeoutForTool(toolName: ToolName): number {
  const tool = TOOL_CATALOG.find((entry) => entry.name === toolName);
  if (!tool) {
    throw new UnityMcpError("ERR_UNKNOWN_COMMAND", `Unknown tool: ${toolName}`);
  }
  return tool.default_timeout_ms;
}

class RuntimeState extends EventEmitter {
  private _serverState: ServerState = "booting";
  private _editorState: EditorState = "unknown";
  private _connected = false;
  private _lastEditorStatusSeq = 0;

  get snapshot(): {
    server_state: ServerState;
    editor_state: EditorState;
    connected: boolean;
    last_editor_status_seq: number;
  } {
    return {
      server_state: this._serverState,
      editor_state: this._connected ? this._editorState : "unknown",
      connected: this._connected,
      last_editor_status_seq: this._lastEditorStatusSeq,
    };
  }

  setServerState(next: ServerState): void {
    if (this._serverState === next) {
      return;
    }
    this._serverState = next;
    this.emit("state_changed");
  }

  onConnected(initialEditorState: EditorState): void {
    this._connected = true;
    this._editorState = initialEditorState;
    this.setServerState("ready");
    this.emit("state_changed");
  }

  onDisconnected(): void {
    this._connected = false;
    this._editorState = "unknown";
    if (this._serverState !== "stopping" && this._serverState !== "stopped") {
      this.setServerState("waiting_editor");
    }
    this.emit("state_changed");
  }

  onEditorStatus(editorState: EditorState, seq: number): void {
    if (seq <= this._lastEditorStatusSeq) {
      return;
    }
    this._lastEditorStatusSeq = seq;
    this._editorState = editorState;
    this.emit("state_changed");
  }

  onPong(editorState?: EditorState, seq?: number): void {
    if (editorState) {
      this._editorState = editorState;
    }
    if (typeof seq === "number" && seq > this._lastEditorStatusSeq) {
      this._lastEditorStatusSeq = seq;
    }
    this.emit("state_changed");
  }

  isEditorReady(): boolean {
    return this._connected && this._editorState === "ready";
  }

  waitForEditorReady(timeoutMs: number): Promise<boolean> {
    if (this.isEditorReady()) {
      return Promise.resolve(true);
    }

    return new Promise((resolve) => {
      const onChange = (): void => {
        if (this.isEditorReady()) {
          cleanup();
          resolve(true);
        }
      };

      const timer = setTimeout(() => {
        cleanup();
        resolve(false);
      }, timeoutMs);

      const cleanup = (): void => {
        clearTimeout(timer);
        this.off("state_changed", onChange);
      };

      this.on("state_changed", onChange);
    });
  }
}

class FifoScheduler {
  private running = false;

  private readonly queue: Array<{
    task: () => Promise<unknown>;
    resolve: (value: unknown) => void;
    reject: (reason?: unknown) => void;
  }> = [];

  enqueue<T>(task: () => Promise<T>): Promise<T> {
    const pendingCount = this.queue.length + (this.running ? 1 : 0);
    if (pendingCount >= QUEUE_MAX_SIZE) {
      return Promise.reject(new UnityMcpError("ERR_QUEUE_FULL", "Queue is full"));
    }

    return new Promise<T>((resolve, reject) => {
      this.queue.push({
        task: async () => task(),
        resolve: (value) => resolve(value as T),
        reject,
      });
      this.drain().catch((error: unknown) => {
        log("ERROR", "Failed to drain queue", {
          error: error instanceof Error ? error.message : String(error),
        });
      });
    });
  }

  private async drain(): Promise<void> {
    if (this.running) {
      return;
    }

    const next = this.queue.shift();
    if (!next) {
      return;
    }

    this.running = true;
    try {
      const result = await next.task();
      next.resolve(result);
    } catch (error) {
      next.reject(error);
    } finally {
      this.running = false;
      if (this.queue.length > 0) {
        await this.drain();
      }
    }
  }
}

interface PendingRequest<T> {
  expectedType: string;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
  timer: NodeJS.Timeout;
}

interface JobRecord {
  job_id: string;
  state: JobState;
  result: unknown;
}

class UnityBridge {
  private socket: WebSocket | null = null;
  private heartbeatInterval: NodeJS.Timeout | null = null;
  private heartbeatTimeout: NodeJS.Timeout | null = null;

  private readonly pendingRequests = new Map<string, PendingRequest<Record<string, unknown>>>();
  private readonly jobs = new Map<string, JobRecord>();

  constructor(
    private readonly runtimeState: RuntimeState,
    private readonly scheduler: FifoScheduler,
  ) {}

  attachWebSocketServer(wss: WebSocketServer): void {
    wss.on("connection", (socket, request) => {
      if (this.socket) {
        log("WARN", "Replacing existing Unity connection", {
          remote: request.socket.remoteAddress,
        });
        this.socket.close(1000, "superseded");
      }

      this.socket = socket;
      log("INFO", "Unity websocket connected", {
        remote: request.socket.remoteAddress,
      });

      socket.on("message", (raw) => this.onSocketMessage(socket, raw.toString()));
      socket.on("close", () => this.onSocketClosed(socket));
      socket.on("error", (error) => {
        log("ERROR", "Unity websocket error", {
          error: error.message,
        });
      });
    });
  }

  async readConsole(maxEntries: number): Promise<unknown> {
    return this.scheduler.enqueue(async () => {
      await this.ensureEditorReady();
      const response = await this.sendRequest(
        {
          type: "execute",
          protocol_version: PROTOCOL_VERSION,
          request_id: randomUUID(),
          tool_name: "read_console",
          params: {
            max_entries: maxEntries,
          },
          timeout_ms: timeoutForTool("read_console"),
        },
        "result",
        timeoutForTool("read_console"),
      );

      const status = response.status;
      if (status === "ok") {
        return response.result ?? {};
      }

      throw new UnityMcpError("ERR_UNITY_EXECUTION", "Unity returned execution error", {
        response,
      });
    });
  }

  async runTests(mode: "all" | "edit" | "play", filter?: string): Promise<{ job_id: string; state: "queued" }> {
    return this.scheduler.enqueue(async () => {
      await this.ensureEditorReady();
      const response = await this.sendRequest(
        {
          type: "submit_job",
          protocol_version: PROTOCOL_VERSION,
          request_id: randomUUID(),
          tool_name: "run_tests",
          params: {
            mode,
            ...(filter ? { filter } : {}),
          },
          timeout_ms: timeoutForTool("run_tests"),
        },
        "submit_job_result",
        timeoutForTool("run_tests"),
      );

      if (response.status !== "accepted" || typeof response.job_id !== "string") {
        throw new UnityMcpError("ERR_INVALID_RESPONSE", "Invalid submit_job_result payload", { response });
      }

      const record: JobRecord = {
        job_id: response.job_id,
        state: "queued",
        result: null,
      };
      this.jobs.set(record.job_id, record);

      return {
        job_id: record.job_id,
        state: "queued",
      };
    });
  }

  async getJobStatus(jobId: string): Promise<{
    job_id: string;
    state: JobState;
    progress: null;
    result: unknown;
  }> {
    return this.scheduler.enqueue(async () => {
      if (!this.jobs.has(jobId)) {
        throw new UnityMcpError("ERR_JOB_NOT_FOUND", `Unknown job_id: ${jobId}`);
      }

      await this.ensureEditorReady();
      const response = await this.sendRequest(
        {
          type: "get_job_status",
          protocol_version: PROTOCOL_VERSION,
          request_id: randomUUID(),
          job_id: jobId,
        },
        "job_status",
        timeoutForTool("get_job_status"),
      );

      const state = response.state;
      if (!isJobState(state)) {
        throw new UnityMcpError("ERR_INVALID_RESPONSE", "Invalid job_status state", { response });
      }

      const normalized = {
        job_id: jobId,
        state,
        progress: null,
        result: response.result ?? {},
      };

      const existing = this.jobs.get(jobId);
      if (existing) {
        existing.state = normalized.state;
        existing.result = normalized.result;
      }

      return normalized;
    });
  }

  async cancelJob(jobId: string): Promise<{ job_id: string; status: "cancelled" | "cancel_requested" | "rejected" }> {
    return this.scheduler.enqueue(async () => {
      const existing = this.jobs.get(jobId);
      if (!existing) {
        throw new UnityMcpError("ERR_JOB_NOT_FOUND", `Unknown job_id: ${jobId}`);
      }

      // queued の時点ではUnity往復なしで即時cancel可能
      if (existing.state === "queued") {
        existing.state = "cancelled";
        return {
          job_id: jobId,
          status: "cancelled",
        };
      }

      if (isTerminalState(existing.state)) {
        return {
          job_id: jobId,
          status: "rejected",
        };
      }

      await this.ensureEditorReady();
      const response = await this.sendRequest(
        {
          type: "cancel",
          protocol_version: PROTOCOL_VERSION,
          request_id: randomUUID(),
          target_job_id: jobId,
        },
        "cancel_result",
        timeoutForTool("cancel_job"),
      );

      const status = response.status;
      if (status !== "cancelled" && status !== "cancel_requested" && status !== "rejected") {
        throw new UnityMcpError("ERR_INVALID_RESPONSE", "Invalid cancel_result status", { response });
      }

      if (status === "cancelled") {
        existing.state = "cancelled";
      }

      return {
        job_id: jobId,
        status,
      };
    });
  }

  close(): void {
    this.clearHeartbeat();

    for (const pending of this.pendingRequests.values()) {
      clearTimeout(pending.timer);
      pending.reject(new UnityMcpError("ERR_UNITY_DISCONNECTED", "Unity websocket disconnected"));
    }
    this.pendingRequests.clear();

    if (this.socket) {
      this.socket.close(1001, "server-shutdown");
      this.socket = null;
    }

    this.runtimeState.onDisconnected();
  }

  private async ensureEditorReady(): Promise<void> {
    if (this.runtimeState.isEditorReady()) {
      return;
    }

    const resumed = await this.runtimeState.waitForEditorReady(REQUEST_RECONNECT_WAIT_MS);
    if (!resumed) {
      throw new UnityMcpError("ERR_EDITOR_NOT_READY", "Editor is not ready");
    }
  }

  private onSocketClosed(closedSocket: WebSocket): void {
    if (this.socket !== closedSocket) {
      return;
    }

    this.clearHeartbeat();

    for (const pending of this.pendingRequests.values()) {
      clearTimeout(pending.timer);
      pending.reject(new UnityMcpError("ERR_UNITY_DISCONNECTED", "Unity websocket disconnected"));
    }
    this.pendingRequests.clear();

    this.socket = null;
    this.runtimeState.onDisconnected();

    log("WARN", "Unity websocket disconnected");
  }

  private onSocketMessage(sourceSocket: WebSocket, raw: string): void {
    if (this.socket !== sourceSocket) {
      return;
    }

    let message: Record<string, unknown>;

    try {
      message = JSON.parse(raw) as Record<string, unknown>;
    } catch {
      log("WARN", "Received non-JSON message from Unity");
      return;
    }

    if (message.protocol_version !== PROTOCOL_VERSION) {
      this.sendRaw({
        type: "error",
        protocol_version: PROTOCOL_VERSION,
        request_id: message.request_id,
        error: {
          code: "ERR_INVALID_REQUEST",
          message: "protocol_version mismatch",
        },
      });
      this.socket?.close(1002, "protocol-version-mismatch");
      return;
    }

    const type = message.type;
    if (type === "hello") {
      this.handleHello(message);
      return;
    }

    if (type === "editor_status") {
      this.handleEditorStatus(message);
      return;
    }

    if (type === "pong") {
      this.handlePong(message);
      return;
    }

    const requestId = typeof message.request_id === "string" ? message.request_id : null;
    if (requestId) {
      const pending = this.pendingRequests.get(requestId);
      if (pending) {
        if (message.type === "error") {
          const error = message.error as Record<string, unknown> | undefined;
          const code = typeof error?.code === "string" ? error.code : "ERR_UNITY_EXECUTION";
          const detailMessage = typeof error?.message === "string" ? error.message : "Unity returned error";
          clearTimeout(pending.timer);
          this.pendingRequests.delete(requestId);
          pending.reject(new UnityMcpError(code, detailMessage, error?.details));
          return;
        }

        if (message.type !== pending.expectedType) {
          clearTimeout(pending.timer);
          this.pendingRequests.delete(requestId);
          pending.reject(
            new UnityMcpError("ERR_INVALID_RESPONSE", "Unexpected response type", {
              expected: pending.expectedType,
              actual: message.type,
            }),
          );
          return;
        }

        clearTimeout(pending.timer);
        this.pendingRequests.delete(requestId);
        pending.resolve(message);
        return;
      }
    }

    log("WARN", "Unhandled message from Unity", {
      type,
      request_id: requestId,
    });
  }

  private handleHello(message: Record<string, unknown>): void {
    const pluginVersion = typeof message.plugin_version === "string" ? message.plugin_version : "unknown";
    const initialEditorState = toEditorState(message.state);

    this.runtimeState.onConnected(initialEditorState);
    this.startHeartbeat();

    this.sendRaw({
      type: "hello",
      protocol_version: PROTOCOL_VERSION,
      server_version: SERVER_VERSION,
    });

    this.sendRaw({
      type: "capability",
      protocol_version: PROTOCOL_VERSION,
      tools: TOOL_CATALOG,
    });

    log("INFO", "Unity hello received", {
      plugin_version: pluginVersion,
      editor_state: initialEditorState,
    });
  }

  private handleEditorStatus(message: Record<string, unknown>): void {
    const editorState = toEditorState(message.state);
    const seq = typeof message.seq === "number" ? message.seq : 0;
    this.runtimeState.onEditorStatus(editorState, seq);
  }

  private handlePong(message: Record<string, unknown>): void {
    if (this.heartbeatTimeout) {
      clearTimeout(this.heartbeatTimeout);
      this.heartbeatTimeout = null;
    }

    const editorState = toEditorStateOrUndefined(message.editor_state);
    const seq = typeof message.seq === "number" ? message.seq : undefined;
    this.runtimeState.onPong(editorState, seq);
  }

  private startHeartbeat(): void {
    this.clearHeartbeat();

    this.heartbeatInterval = setInterval(() => {
      if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
        return;
      }

      this.sendRaw({
        type: "ping",
        protocol_version: PROTOCOL_VERSION,
      });

      if (this.heartbeatTimeout) {
        clearTimeout(this.heartbeatTimeout);
      }

      this.heartbeatTimeout = setTimeout(() => {
        if (this.socket && this.socket.readyState === WebSocket.OPEN) {
          log("WARN", "Heartbeat timeout. Closing Unity websocket.");
          this.socket.close(1001, "heartbeat-timeout");
        }
      }, HEARTBEAT_TIMEOUT_MS);
    }, HEARTBEAT_INTERVAL_MS);
  }

  private clearHeartbeat(): void {
    if (this.heartbeatInterval) {
      clearInterval(this.heartbeatInterval);
      this.heartbeatInterval = null;
    }

    if (this.heartbeatTimeout) {
      clearTimeout(this.heartbeatTimeout);
      this.heartbeatTimeout = null;
    }
  }

  private async sendRequest(
    message: Record<string, unknown>,
    expectedType: string,
    timeoutMs: number,
  ): Promise<Record<string, unknown>> {
    const requestId = message.request_id;
    if (typeof requestId !== "string") {
      throw new UnityMcpError("ERR_INVALID_REQUEST", "request_id must be string");
    }

    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      throw new UnityMcpError("ERR_UNITY_DISCONNECTED", "Unity websocket is not connected");
    }

    return new Promise<Record<string, unknown>>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(requestId);
        reject(new UnityMcpError("ERR_REQUEST_TIMEOUT", `Unity request timeout: ${requestId}`));
      }, timeoutMs);

      this.pendingRequests.set(requestId, {
        expectedType,
        resolve,
        reject,
        timer,
      });

      this.sendRaw(message);
    });
  }

  private sendRaw(payload: Record<string, unknown>): void {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      throw new UnityMcpError("ERR_UNITY_DISCONNECTED", "Unity websocket is not connected");
    }

    this.socket.send(JSON.stringify(payload));
  }
}

function toEditorState(value: unknown): EditorState {
  if (value === "ready" || value === "compiling" || value === "reloading") {
    return value;
  }
  return "unknown";
}

function toEditorStateOrUndefined(value: unknown): EditorState | undefined {
  if (value === "ready" || value === "compiling" || value === "reloading") {
    return value;
  }
  return undefined;
}

function isJobState(value: unknown): value is JobState {
  return (
    value === "queued" ||
    value === "running" ||
    value === "succeeded" ||
    value === "failed" ||
    value === "timeout" ||
    value === "cancelled"
  );
}

function isTerminalState(value: JobState): boolean {
  return value === "succeeded" || value === "failed" || value === "timeout" || value === "cancelled";
}

function createMcpServer(runtimeState: RuntimeState, bridge: UnityBridge): McpServer {
  const mcpServer = new McpServer({
    name: SERVER_NAME,
    version: SERVER_VERSION,
  });

  mcpServer.registerTool(
    "get_editor_state",
    {
      title: "Get Editor State",
      description: "Returns current server/editor connection state.",
      inputSchema: z.object({}).strict(),
    },
    async () => toolTextPayload(runtimeState.snapshot),
  );

  mcpServer.registerTool(
    "read_console",
    {
      title: "Read Console",
      description: "Reads Unity console entries.",
      inputSchema: z
        .object({
          max_entries: z.number().int().min(1).max(2000).default(200),
        })
        .strict(),
    },
    async ({ max_entries }) => {
      try {
        const result = await bridge.readConsole(max_entries);
        return toolTextPayload(result);
      } catch (error) {
        return toolErrorPayload(error);
      }
    },
  );

  mcpServer.registerTool(
    "run_tests",
    {
      title: "Run Tests",
      description: "Starts Unity tests as a cancellable job.",
      inputSchema: z
        .object({
          mode: z.enum(["all", "edit", "play"]).default("all"),
          filter: z.string().min(1).optional(),
        })
        .strict(),
    },
    async ({ mode, filter }) => {
      try {
        const result = await bridge.runTests(mode, filter);
        return toolTextPayload(result);
      } catch (error) {
        return toolErrorPayload(error);
      }
    },
  );

  mcpServer.registerTool(
    "get_job_status",
    {
      title: "Get Job Status",
      description: "Checks state/result of a submitted test job.",
      inputSchema: z
        .object({
          job_id: z.string().min(1),
        })
        .strict(),
    },
    async ({ job_id }) => {
      try {
        const result = await bridge.getJobStatus(job_id);
        return toolTextPayload(result);
      } catch (error) {
        return toolErrorPayload(error);
      }
    },
  );

  mcpServer.registerTool(
    "cancel_job",
    {
      title: "Cancel Job",
      description: "Requests cancellation for a running/queued job.",
      inputSchema: z
        .object({
          job_id: z.string().min(1),
        })
        .strict(),
    },
    async ({ job_id }) => {
      try {
        const result = await bridge.cancelJob(job_id);
        return toolTextPayload(result);
      } catch (error) {
        return toolErrorPayload(error);
      }
    },
  );

  return mcpServer;
}

async function main(): Promise<void> {
  const config = parseConfig(process.argv.slice(2));

  const runtimeState = new RuntimeState();
  runtimeState.setServerState("waiting_editor");

  const scheduler = new FifoScheduler();
  const bridge = new UnityBridge(runtimeState, scheduler);

  const app = express();
  app.use(express.json({ limit: `${MAX_MESSAGE_BYTES}b` }));

  app.use((error: unknown, _req: express.Request, res: express.Response, next: express.NextFunction) => {
    if (error) {
      const message = error instanceof Error ? error.message : "Invalid JSON";
      res.status(400).json({
        jsonrpc: "2.0",
        error: {
          code: -32600,
          message,
        },
        id: null,
      });
      return;
    }
    next();
  });

  const httpServer = http.createServer(app);
  const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_MESSAGE_BYTES });
  bridge.attachWebSocketServer(wss);

  const sessions = new Map<
    string,
    {
      transport: StreamableHTTPServerTransport;
      server: McpServer;
    }
  >();

  const getSessionId = (req: express.Request): string | undefined => {
    const header = req.headers["mcp-session-id"];
    return typeof header === "string" && header.length > 0 ? header : undefined;
  };

  app.post(MCP_HTTP_PATH, async (req, res) => {
    try {
      const sessionId = getSessionId(req);
      let session = sessionId ? sessions.get(sessionId) : undefined;

      // Initialize request creates a brand-new MCP server + transport per session.
      if (!session && !sessionId && isInitializeRequest(req.body)) {
        const mcpServer = createMcpServer(runtimeState, bridge);
        const transport = new StreamableHTTPServerTransport({
          sessionIdGenerator: () => randomUUID(),
          onsessioninitialized: (newSessionId) => {
            sessions.set(newSessionId, {
              transport,
              server: mcpServer,
            });
            log("INFO", "MCP session initialized", { session_id: newSessionId });
          },
        });

        transport.onclose = (): void => {
          if (transport.sessionId) {
            sessions.delete(transport.sessionId);
          }
        };

        await mcpServer.connect(transport);
        session = {
          transport,
          server: mcpServer,
        };
      }

      if (!session) {
        res.status(400).json({
          jsonrpc: "2.0",
          error: {
            code: -32000,
            message: "Bad Request: No valid session ID provided",
          },
          id: null,
        });
        return;
      }

      await session.transport.handleRequest(req, res, req.body);
    } catch (error) {
      log("ERROR", "POST /mcp failed", {
        error: error instanceof Error ? error.message : String(error),
      });

      if (!res.headersSent) {
        res.status(500).json({
          jsonrpc: "2.0",
          error: {
            code: -32603,
            message: "Internal server error",
          },
          id: null,
        });
      }
    }
  });

  app.get(MCP_HTTP_PATH, async (req, res) => {
    const sessionId = getSessionId(req);
    const session = sessionId ? sessions.get(sessionId) : undefined;
    if (!session) {
      res.status(400).json({
        jsonrpc: "2.0",
        error: {
          code: -32000,
          message: "Bad Request: No valid session ID provided",
        },
        id: null,
      });
      return;
    }

    await session.transport.handleRequest(req, res);
  });

  app.delete(MCP_HTTP_PATH, async (req, res) => {
    const sessionId = getSessionId(req);
    const session = sessionId ? sessions.get(sessionId) : undefined;
    if (!session) {
      res.status(400).json({
        jsonrpc: "2.0",
        error: {
          code: -32000,
          message: "Bad Request: No valid session ID provided",
        },
        id: null,
      });
      return;
    }

    await session.transport.handleRequest(req, res);
  });

  httpServer.on("upgrade", (request, socket, head) => {
    const url = request.url ?? "";
    if (!url.startsWith(UNITY_WS_PATH)) {
      socket.destroy();
      return;
    }

    wss.handleUpgrade(request, socket, head, (ws) => {
      wss.emit("connection", ws, request);
    });
  });

  const shutdown = async (signal: string): Promise<void> => {
    if (runtimeState.snapshot.server_state === "stopping" || runtimeState.snapshot.server_state === "stopped") {
      return;
    }

    log("INFO", `Received ${signal}. Shutting down.`);
    runtimeState.setServerState("stopping");

    bridge.close();

    const activeSessions = Array.from(sessions.values());
    for (const session of activeSessions) {
      await session.transport.close();
    }
    sessions.clear();

    await new Promise<void>((resolve) => {
      httpServer.close(() => resolve());
    });

    runtimeState.setServerState("stopped");
    process.exit(0);
  };

  process.on("SIGINT", () => {
    void shutdown("SIGINT");
  });
  process.on("SIGTERM", () => {
    void shutdown("SIGTERM");
  });

  await new Promise<void>((resolve) => {
    httpServer.listen(config.port, HOST, () => resolve());
  });

  log("INFO", "Unity MCP server started", {
    host: HOST,
    port: config.port,
    mcp_path: MCP_HTTP_PATH,
    unity_ws_path: UNITY_WS_PATH,
    server_state: runtimeState.snapshot.server_state,
  });
}

main().catch((error: unknown) => {
  if (error instanceof UnityMcpError && error.code === "ERR_CONFIG_VALIDATION") {
    log("ERROR", "Configuration validation failed", {
      code: error.code,
      message: error.message,
      details: error.details,
    });
    process.exit(1);
  }

  log("ERROR", "Server crashed", {
    error: error instanceof Error ? error.message : String(error),
  });
  process.exit(1);
});
