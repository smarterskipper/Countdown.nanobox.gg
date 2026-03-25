import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { ProxmoxApi } from "./proxmox-api.js";
import { SshClient } from "./ssh-client.js";

// ── Config from environment ───────────────────────────────────────────────────
const cfg = {
  proxmoxHost:     requireEnv("PROXMOX_HOST"),
  proxmoxApiToken: requireEnv("PROXMOX_API_TOKEN"), // user@pam!tokenid=uuid
  proxmoxNode:     process.env.PROXMOX_NODE ?? "pve",
  sshUser:         process.env.PROXMOX_SSH_USER ?? "root",
  sshKeyPath:      requireEnv("PROXMOX_SSH_KEY_PATH"),
  sshPort:         parseInt(process.env.PROXMOX_SSH_PORT ?? "22", 10),
};

function requireEnv(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`Missing required env var: ${name}`);
  return v;
}

const api = new ProxmoxApi(cfg.proxmoxHost, cfg.proxmoxApiToken, cfg.proxmoxNode);
const ssh = new SshClient({
  host: cfg.proxmoxHost,
  port: cfg.sshPort,
  username: cfg.sshUser,
  privateKeyPath: cfg.sshKeyPath,
});

// ── MCP server ────────────────────────────────────────────────────────────────
const server = new McpServer({
  name: "proxmox",
  version: "1.0.0",
});

// ── Tool: list_lxcs ───────────────────────────────────────────────────────────
server.tool(
  "list_lxcs",
  "List all LXC containers on the Proxmox node with their status, CPU, memory",
  {},
  async () => {
    const lxcs = await api.listLxcs();
    const rows = lxcs.map((c) =>
      `${c.vmid.toString().padEnd(6)} ${c.name?.padEnd(24) ?? "<unnamed>".padEnd(24)} ` +
      `${c.status.padEnd(10)} ${c.cpus}cpu  ` +
      `${Math.round(c.maxmem / 1024 / 1024)}MB`
    );
    return {
      content: [{ type: "text", text: ["VMID   NAME                     STATUS     RESOURCES", ...rows].join("\n") }],
    };
  }
);

// ── Tool: get_lxc_status ──────────────────────────────────────────────────────
server.tool(
  "get_lxc_status",
  "Get detailed status of a specific LXC container",
  { vmid: z.number().describe("Container ID") },
  async ({ vmid }) => {
    const status = await api.getLxcStatus(vmid);
    return { content: [{ type: "text", text: JSON.stringify(status, null, 2) }] };
  }
);

// ── Tool: create_lxc ─────────────────────────────────────────────────────────
server.tool(
  "create_lxc",
  "Create a new LXC container on Proxmox. Waits for the task to complete before returning.",
  {
    vmid:          z.number().optional().describe("Container ID (omit to auto-assign)"),
    hostname:      z.string().describe("Hostname for the container"),
    ostemplate:    z.string().describe('Template, e.g. "local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst"'),
    storage:       z.string().describe('Storage pool, e.g. "local-lvm"'),
    rootfsSize:    z.number().default(10).describe("Root filesystem size in GB"),
    memory:        z.number().default(2048).describe("RAM in MB"),
    cores:         z.number().default(2).describe("CPU cores"),
    password:      z.string().describe("Root password for the container"),
    sshPublicKey:  z.string().optional().describe("SSH public key to authorize for root"),
    netIp:         z.string().default("dhcp").describe('IP address with prefix e.g. "192.168.1.50/24" or "dhcp"'),
    netGw:         z.string().optional().describe("Gateway IP (required if not using DHCP)"),
    bridge:        z.string().default("vmbr0").describe("Network bridge"),
    nameserver:    z.string().optional().describe("DNS nameserver"),
  },
  async (opts) => {
    const vmid = opts.vmid ?? await api.getNextVmid();
    const upid = await api.createLxc({ ...opts, vmid });
    await api.waitForTask(upid, 120_000);
    return {
      content: [{ type: "text", text: `✓ Container ${vmid} (${opts.hostname}) created successfully.` }],
    };
  }
);

// ── Tool: start_lxc ──────────────────────────────────────────────────────────
server.tool(
  "start_lxc",
  "Start a stopped LXC container",
  { vmid: z.number().describe("Container ID") },
  async ({ vmid }) => {
    const upid = await api.startLxc(vmid);
    await api.waitForTask(upid, 30_000);
    return { content: [{ type: "text", text: `✓ Container ${vmid} started.` }] };
  }
);

// ── Tool: stop_lxc ───────────────────────────────────────────────────────────
server.tool(
  "stop_lxc",
  "Stop a running LXC container",
  { vmid: z.number().describe("Container ID") },
  async ({ vmid }) => {
    const upid = await api.stopLxc(vmid);
    await api.waitForTask(upid, 30_000);
    return { content: [{ type: "text", text: `✓ Container ${vmid} stopped.` }] };
  }
);

// ── Tool: destroy_lxc ────────────────────────────────────────────────────────
server.tool(
  "destroy_lxc",
  "Permanently destroy an LXC container. Stop it first if running.",
  { vmid: z.number().describe("Container ID") },
  async ({ vmid }) => {
    const upid = await api.destroyLxc(vmid);
    await api.waitForTask(upid, 60_000);
    return { content: [{ type: "text", text: `✓ Container ${vmid} destroyed.` }] };
  }
);

// ── Tool: exec_lxc ───────────────────────────────────────────────────────────
server.tool(
  "exec_lxc",
  "Run a shell command inside a running LXC container via pct exec. Returns stdout, stderr, and exit code.",
  {
    vmid:       z.number().describe("Container ID"),
    command:    z.string().describe("Shell command to run inside the container"),
    timeoutMs:  z.number().default(120_000).describe("Timeout in milliseconds"),
  },
  async ({ vmid, command, timeoutMs }) => {
    const result = await ssh.execInLxc(vmid, command, timeoutMs);
    const text = [
      result.stdout ? `STDOUT:\n${result.stdout}` : "",
      result.stderr ? `STDERR:\n${result.stderr}` : "",
      `Exit code: ${result.code}`,
    ].filter(Boolean).join("\n\n");
    return { content: [{ type: "text", text }] };
  }
);

// ── Tool: exec_host ──────────────────────────────────────────────────────────
server.tool(
  "exec_host",
  "Run a shell command on the Proxmox HOST itself (not inside a container). Use for pct/qm commands and host-level ops.",
  {
    command:   z.string().describe("Shell command to run on the Proxmox host"),
    timeoutMs: z.number().default(60_000).describe("Timeout in milliseconds"),
  },
  async ({ command, timeoutMs }) => {
    const result = await ssh.exec(command, timeoutMs);
    const text = [
      result.stdout ? `STDOUT:\n${result.stdout}` : "",
      result.stderr ? `STDERR:\n${result.stderr}` : "",
      `Exit code: ${result.code}`,
    ].filter(Boolean).join("\n\n");
    return { content: [{ type: "text", text }] };
  }
);

// ── Tool: write_file_in_lxc ──────────────────────────────────────────────────
server.tool(
  "write_file_in_lxc",
  "Write a text file directly into a running LXC container",
  {
    vmid:       z.number().describe("Container ID"),
    remotePath: z.string().describe("Absolute path inside the container, e.g. /etc/nginx/sites-available/app"),
    content:    z.string().describe("File contents to write"),
  },
  async ({ vmid, remotePath, content }) => {
    await ssh.writeFileInLxc(vmid, content, remotePath);
    return { content: [{ type: "text", text: `✓ Wrote ${remotePath} in container ${vmid}` }] };
  }
);

// ── Tool: get_service_logs ────────────────────────────────────────────────────
server.tool(
  "get_service_logs",
  "Get systemd journal logs for a service running inside an LXC container",
  {
    vmid:    z.number().describe("Container ID"),
    service: z.string().describe("systemd service name, e.g. homecountdown"),
    lines:   z.number().default(50).describe("Number of recent log lines to return"),
  },
  async ({ vmid, service, lines }) => {
    const result = await ssh.execInLxc(vmid, `journalctl -u ${service} -n ${lines} --no-pager`);
    return { content: [{ type: "text", text: result.stdout || result.stderr || "(no output)" }] };
  }
);

// ── Tool: list_templates ─────────────────────────────────────────────────────
server.tool(
  "list_templates",
  "List available LXC OS templates on a Proxmox storage",
  { storage: z.string().default("local").describe("Storage name") },
  async ({ storage }) => {
    const templates = await api.listTemplates(storage);
    if (templates.length === 0) return { content: [{ type: "text", text: "No templates found." }] };
    const lines = templates.map((t) => t.volid);
    return { content: [{ type: "text", text: lines.join("\n") }] };
  }
);

// ── Tool: get_next_vmid ──────────────────────────────────────────────────────
server.tool(
  "get_next_vmid",
  "Get the next available VMID on the Proxmox cluster",
  {},
  async () => {
    const id = await api.getNextVmid();
    return { content: [{ type: "text", text: `Next available VMID: ${id}` }] };
  }
);

// ── Start ─────────────────────────────────────────────────────────────────────
const transport = new StdioServerTransport();
await server.connect(transport);
