import axios, { type AxiosInstance } from "axios";
import https from "https";

export interface LxcSummary {
  vmid: number;
  name: string;
  status: string;
  cpus: number;
  maxmem: number;
  maxdisk: number;
  uptime: number;
}

export interface CreateLxcOptions {
  vmid: number;
  hostname: string;
  ostemplate: string;   // e.g. "local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst"
  storage: string;      // e.g. "local-lvm"
  rootfsSize: number;   // GB
  memory: number;       // MB
  cores: number;
  password: string;
  sshPublicKey?: string;
  netIp?: string;       // "dhcp" or "192.168.1.50/24"
  netGw?: string;       // required if static IP
  bridge?: string;      // default "vmbr0"
  nameserver?: string;
  unprivileged?: boolean;
  startOnBoot?: boolean;
}

export class ProxmoxApi {
  private http: AxiosInstance;
  readonly defaultNode: string;

  constructor(host: string, apiToken: string, node: string) {
    this.defaultNode = node;
    this.http = axios.create({
      baseURL: `https://${host}:8006/api2/json`,
      headers: {
        Authorization: `PVEAPIToken=${apiToken}`,
        "Content-Type": "application/json",
      },
      // Self-signed cert is common on homelab Proxmox
      httpsAgent: new https.Agent({ rejectUnauthorized: false }),
      timeout: 30_000,
    });
  }

  async listNodes(): Promise<Array<{ node: string; status: string; cpu: number; maxcpu: number }>> {
    const { data } = await this.http.get("/nodes");
    return data.data;
  }

  async listLxcs(node?: string): Promise<LxcSummary[]> {
    const n = node ?? this.defaultNode;
    const { data } = await this.http.get(`/nodes/${n}/lxc`);
    return data.data.map((c: LxcSummary) => ({
      vmid: c.vmid,
      name: c.name,
      status: c.status,
      cpus: c.cpus,
      maxmem: c.maxmem,
      maxdisk: c.maxdisk,
      uptime: c.uptime,
    }));
  }

  async listAllLxcs(): Promise<Array<LxcSummary & { node: string }>> {
    const nodes = await this.listNodes();
    const results = await Promise.all(
      nodes.filter(n => n.status === "online").map(async (n) => {
        const lxcs = await this.listLxcs(n.node);
        return lxcs.map(c => ({ ...c, node: n.node }));
      })
    );
    return results.flat();
  }

  async getLxcStatus(vmid: number, node?: string): Promise<Record<string, unknown>> {
    const n = node ?? this.defaultNode;
    const { data } = await this.http.get(`/nodes/${n}/lxc/${vmid}/status/current`);
    return data.data;
  }

  async createLxc(opts: CreateLxcOptions, node?: string): Promise<string> {
    const netConfig = opts.netIp === "dhcp" || !opts.netIp
      ? `name=eth0,bridge=${opts.bridge ?? "vmbr0"},ip=dhcp`
      : `name=eth0,bridge=${opts.bridge ?? "vmbr0"},ip=${opts.netIp},gw=${opts.netGw}`;

    const payload: Record<string, unknown> = {
      vmid: opts.vmid,
      hostname: opts.hostname,
      ostemplate: opts.ostemplate,
      storage: opts.storage,
      rootfs: `${opts.storage}:${opts.rootfsSize}`,
      memory: opts.memory,
      cores: opts.cores,
      password: opts.password,
      net0: netConfig,
      unprivileged: opts.unprivileged !== false ? 1 : 0,
      onboot: opts.startOnBoot !== false ? 1 : 0,
      features: "nesting=1",   // needed for Playwright/Chromium
    };

    if (opts.sshPublicKey) payload["ssh-public-keys"] = opts.sshPublicKey;
    if (opts.nameserver) payload.nameserver = opts.nameserver;

    const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc`, payload);
    return data.data; // task ID (UPID)
  }

  async startLxc(vmid: number, node?: string): Promise<string> {
    const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}/status/start`);
    return data.data;
  }

  async stopLxc(vmid: number, node?: string): Promise<string> {
    const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}/status/stop`);
    return data.data;
  }

  async rebootLxc(vmid: number, node?: string): Promise<string> {
    const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}/status/reboot`);
    return data.data;
  }

  async destroyLxc(vmid: number, node?: string): Promise<string> {
    const { data } = await this.http.delete(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}`);
    return data.data;
  }

  async getTaskStatus(upid: string, node?: string): Promise<{ status: string; exitstatus?: string }> {
    const encoded = encodeURIComponent(upid);
    const { data } = await this.http.get(`/nodes/${node ?? this.defaultNode}/tasks/${encoded}/status`);
    return data.data;
  }

  async waitForTask(upid: string, timeoutMs = 120_000, node?: string): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const task = await this.getTaskStatus(upid, node);
      if (task.status === "stopped") {
        if (task.exitstatus !== "OK") {
          throw new Error(`Task ${upid} failed: ${task.exitstatus}`);
        }
        return;
      }
      await new Promise((r) => setTimeout(r, 2000));
    }
    throw new Error(`Task ${upid} timed out after ${timeoutMs}ms`);
  }

  async listTemplates(storage: string, node?: string): Promise<Array<{ volid: string; content: string }>> {
    const n = node ?? this.defaultNode;
    const { data } = await this.http.get(`/nodes/${n}/storage/${storage}/content`, {
      params: { content: "vztmpl" },
    });
    return data.data ?? [];
  }

  async downloadTemplate(storage: string, template: string, node?: string): Promise<string> {
    const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/aplinfo`, {
      storage,
      template,
    });
    return data.data;
  }

  async getNextVmid(): Promise<number> {
    const { data } = await this.http.get("/cluster/nextid");
    return parseInt(data.data, 10);
  }
}
