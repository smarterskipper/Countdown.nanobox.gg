import axios from "axios";
import https from "https";
export class ProxmoxApi {
    http;
    defaultNode;
    constructor(host, apiToken, node) {
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
    async listNodes() {
        const { data } = await this.http.get("/nodes");
        return data.data;
    }
    async listLxcs(node) {
        const n = node ?? this.defaultNode;
        const { data } = await this.http.get(`/nodes/${n}/lxc`);
        return data.data.map((c) => ({
            vmid: c.vmid,
            name: c.name,
            status: c.status,
            cpus: c.cpus,
            maxmem: c.maxmem,
            maxdisk: c.maxdisk,
            uptime: c.uptime,
        }));
    }
    async listAllLxcs() {
        const nodes = await this.listNodes();
        const results = await Promise.all(nodes.filter(n => n.status === "online").map(async (n) => {
            const lxcs = await this.listLxcs(n.node);
            return lxcs.map(c => ({ ...c, node: n.node }));
        }));
        return results.flat();
    }
    async getLxcStatus(vmid, node) {
        const n = node ?? this.defaultNode;
        const { data } = await this.http.get(`/nodes/${n}/lxc/${vmid}/status/current`);
        return data.data;
    }
    async createLxc(opts, node) {
        const netConfig = opts.netIp === "dhcp" || !opts.netIp
            ? `name=eth0,bridge=${opts.bridge ?? "vmbr0"},ip=dhcp`
            : `name=eth0,bridge=${opts.bridge ?? "vmbr0"},ip=${opts.netIp},gw=${opts.netGw}`;
        const payload = {
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
            features: "nesting=1", // needed for Playwright/Chromium
        };
        if (opts.sshPublicKey)
            payload["ssh-public-keys"] = opts.sshPublicKey;
        if (opts.nameserver)
            payload.nameserver = opts.nameserver;
        const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc`, payload);
        return data.data; // task ID (UPID)
    }
    async startLxc(vmid, node) {
        const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}/status/start`);
        return data.data;
    }
    async stopLxc(vmid, node) {
        const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}/status/stop`);
        return data.data;
    }
    async rebootLxc(vmid, node) {
        const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}/status/reboot`);
        return data.data;
    }
    async destroyLxc(vmid, node) {
        const { data } = await this.http.delete(`/nodes/${node ?? this.defaultNode}/lxc/${vmid}`);
        return data.data;
    }
    async getTaskStatus(upid, node) {
        const encoded = encodeURIComponent(upid);
        const { data } = await this.http.get(`/nodes/${node ?? this.defaultNode}/tasks/${encoded}/status`);
        return data.data;
    }
    async waitForTask(upid, timeoutMs = 120_000, node) {
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
    async listTemplates(storage, node) {
        const n = node ?? this.defaultNode;
        const { data } = await this.http.get(`/nodes/${n}/storage/${storage}/content`, {
            params: { content: "vztmpl" },
        });
        return data.data ?? [];
    }
    async downloadTemplate(storage, template, node) {
        const { data } = await this.http.post(`/nodes/${node ?? this.defaultNode}/aplinfo`, {
            storage,
            template,
        });
        return data.data;
    }
    async getNextVmid() {
        const { data } = await this.http.get("/cluster/nextid");
        return parseInt(data.data, 10);
    }
}
