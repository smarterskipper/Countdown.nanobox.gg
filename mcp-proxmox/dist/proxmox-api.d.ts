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
    readonly defaultNode: string;
    constructor(host: string, apiToken: string, node: string);
    listNodes(): Promise<Array<{
        node: string;
        status: string;
        cpu: number;
        maxcpu: number;
    }>>;
    listLxcs(node?: string): Promise<LxcSummary[]>;
    listAllLxcs(): Promise<Array<LxcSummary & {
        node: string;
    }>>;
    getLxcStatus(vmid: number, node?: string): Promise<Record<string, unknown>>;
    createLxc(opts: CreateLxcOptions, node?: string): Promise<string>;
    startLxc(vmid: number, node?: string): Promise<string>;
    stopLxc(vmid: number, node?: string): Promise<string>;
    rebootLxc(vmid: number, node?: string): Promise<string>;
    destroyLxc(vmid: number, node?: string): Promise<string>;
    getTaskStatus(upid: string, node?: string): Promise<{
        status: string;
        exitstatus?: string;
    }>;
    waitForTask(upid: string, timeoutMs?: number, node?: string): Promise<void>;
    listTemplates(storage: string, node?: string): Promise<Array<{
        volid: string;
        content: string;
    }>>;
    downloadTemplate(storage: string, template: string, node?: string): Promise<string>;
    getNextVmid(): Promise<number>;
}
