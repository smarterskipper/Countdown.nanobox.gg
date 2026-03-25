import { Client } from "ssh2";
import fs from "fs";
export class SshClient {
    config;
    constructor(config) {
        this.config = config;
    }
    /** Run a command on the Proxmox HOST (not inside a container) */
    exec(command, timeoutMs = 60_000) {
        return new Promise((resolve, reject) => {
            const conn = new Client();
            let stdout = "";
            let stderr = "";
            const timer = setTimeout(() => {
                conn.destroy();
                reject(new Error(`SSH command timed out after ${timeoutMs}ms: ${command}`));
            }, timeoutMs);
            conn
                .on("ready", () => {
                conn.exec(command, (err, stream) => {
                    if (err) {
                        clearTimeout(timer);
                        conn.end();
                        return reject(err);
                    }
                    stream
                        .on("close", (code) => {
                        clearTimeout(timer);
                        conn.end();
                        resolve({ stdout: stdout.trim(), stderr: stderr.trim(), code: code ?? 0 });
                    })
                        .on("data", (data) => { stdout += data.toString(); })
                        .stderr.on("data", (data) => { stderr += data.toString(); });
                });
            })
                .on("error", (err) => {
                clearTimeout(timer);
                reject(err);
            })
                .connect({
                host: this.config.host,
                port: this.config.port ?? 22,
                username: this.config.username,
                privateKey: fs.readFileSync(this.config.privateKeyPath),
            });
        });
    }
    /**
     * Run a command INSIDE an LXC container.
     * Proxmox's `pct exec` runs in the container's namespace.
     * Note: the container must be running.
     */
    execInLxc(vmid, command, timeoutMs = 120_000) {
        // pct exec pipes stdin/stdout via the container's init.
        // We use bash -c so we can pass complex commands with pipes/redirects.
        const escaped = command.replace(/'/g, `'\\''`);
        return this.exec(`pct exec ${vmid} -- bash -c '${escaped}'`, timeoutMs);
    }
    /** Upload a local file into a running LXC via pct push */
    async pushFile(vmid, localPath, remotePath) {
        const result = await this.exec(`pct push ${vmid} ${localPath} ${remotePath}`, 60_000);
        if (result.code !== 0) {
            throw new Error(`pct push failed (${result.code}): ${result.stderr}`);
        }
    }
    /** Write a string as a file inside the LXC using pct exec + tee */
    async writeFileInLxc(vmid, content, remotePath) {
        // Escape for bash heredoc
        const b64 = Buffer.from(content).toString("base64");
        const cmd = `echo '${b64}' | base64 -d | tee ${remotePath} > /dev/null`;
        const result = await this.execInLxc(vmid, cmd);
        if (result.code !== 0) {
            throw new Error(`writeFileInLxc failed: ${result.stderr}`);
        }
    }
}
