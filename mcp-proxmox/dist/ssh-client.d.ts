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
export declare class SshClient {
    private config;
    constructor(config: SshConfig);
    /** Run a command on the Proxmox HOST (not inside a container) */
    exec(command: string, timeoutMs?: number): Promise<ExecResult>;
    /**
     * Run a command INSIDE an LXC container.
     * Proxmox's `pct exec` runs in the container's namespace.
     * Note: the container must be running.
     */
    execInLxc(vmid: number, command: string, timeoutMs?: number): Promise<ExecResult>;
    /** Upload a local file into a running LXC via pct push */
    pushFile(vmid: number, localPath: string, remotePath: string): Promise<void>;
    /** Write a string as a file inside the LXC using pct exec + tee */
    writeFileInLxc(vmid: number, content: string, remotePath: string): Promise<void>;
}
