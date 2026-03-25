import { Client } from "ssh2";
import fs from "fs";

export interface SshConfig {
  host: string;
  port?: number;
  username: string;
  privateKeyPath: string;
}

export interface ExecResult {
  stdout: string;
  stderr: string;
  code: number;
}

export class SshClient {
  constructor(private config: SshConfig) {}

  /** Run a command on the Proxmox HOST (not inside a container) */
  exec(command: string, timeoutMs = 60_000): Promise<ExecResult> {
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
              .on("close", (code: number) => {
                clearTimeout(timer);
                conn.end();
                resolve({ stdout: stdout.trim(), stderr: stderr.trim(), code: code ?? 0 });
              })
              .on("data", (data: Buffer) => { stdout += data.toString(); })
              .stderr.on("data", (data: Buffer) => { stderr += data.toString(); });
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
  execInLxc(vmid: number, command: string, timeoutMs = 120_000): Promise<ExecResult> {
    // pct exec pipes stdin/stdout via the container's init.
    // We use bash -c so we can pass complex commands with pipes/redirects.
    const escaped = command.replace(/'/g, `'\\''`);
    return this.exec(`pct exec ${vmid} -- bash -c '${escaped}'`, timeoutMs);
  }

  /** Upload a local file into a running LXC via pct push */
  async pushFile(vmid: number, localPath: string, remotePath: string): Promise<void> {
    const result = await this.exec(`pct push ${vmid} ${localPath} ${remotePath}`, 60_000);
    if (result.code !== 0) {
      throw new Error(`pct push failed (${result.code}): ${result.stderr}`);
    }
  }

  /** Write a string as a file inside the LXC using pct exec + tee */
  async writeFileInLxc(vmid: number, content: string, remotePath: string): Promise<void> {
    // Escape for bash heredoc
    const b64 = Buffer.from(content).toString("base64");
    const cmd = `echo '${b64}' | base64 -d | tee ${remotePath} > /dev/null`;
    const result = await this.execInLxc(vmid, cmd);
    if (result.code !== 0) {
      throw new Error(`writeFileInLxc failed: ${result.stderr}`);
    }
  }
}
