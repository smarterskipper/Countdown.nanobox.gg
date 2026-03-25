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
    ostemplate: string;
    storage: string;
    rootfsSize: number;
    memory: number;
    cores: number;
    password: string;
    sshPublicKey?: string;
    netIp?: string;
    netGw?: string;
    bridge?: string;
    nameserver?: string;
    unprivileged?: boolean;
    startOnBoot?: boolean;
}
export declare class ProxmoxApi {
    private http;
    private node;
    constructor(host: string, apiToken: string, node: string);
    listNodes(): Promise<Array<{
        node: string;
        status: string;
        cpu: number;
        maxcpu: number;
    }>>;
    listLxcs(): Promise<LxcSummary[]>;
    getLxcStatus(vmid: number): Promise<Record<string, unknown>>;
    createLxc(opts: CreateLxcOptions): Promise<string>;
    startLxc(vmid: number): Promise<string>;
    stopLxc(vmid: number): Promise<string>;
    rebootLxc(vmid: number): Promise<string>;
    destroyLxc(vmid: number): Promise<string>;
    getTaskStatus(upid: string): Promise<{
        status: string;
        exitstatus?: string;
    }>;
    waitForTask(upid: string, timeoutMs?: number): Promise<void>;
    listTemplates(storage: string): Promise<Array<{
        volid: string;
        content: string;
    }>>;
    downloadTemplate(storage: string, template: string): Promise<string>;
    getNextVmid(): Promise<number>;
}
