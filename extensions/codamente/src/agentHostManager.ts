import { ChildProcess, spawn } from 'child_process';
import * as vscode from 'vscode';

/**
 * Manages the agent host child process lifecycle.
 *
 * Spawns `npx agent-host` (or a configured binary) on the specified port
 * and monitors the process for unexpected exits.
 */
export class AgentHostManager {
	private process: ChildProcess | undefined;
	private readonly port: number;
	private readonly outputChannel: vscode.OutputChannel;

	constructor(port: number) {
		this.port = port;
		this.outputChannel = vscode.window.createOutputChannel('Codamente Agent Host');
	}

	get isRunning(): boolean {
		return this.process !== undefined && this.process.exitCode === null;
	}

	/**
	 * Start the agent host process.
	 *
	 * Spawns `npx @anthropic-ai/agent-host` with the configured port.
	 * The exact binary can be overridden via the `CODAMENTE_AGENT_HOST_CMD`
	 * environment variable (space-separated command + args).
	 */
	async start(): Promise<void> {
		if (this.isRunning) {
			return;
		}

		const cmdEnv = process.env['CODAMENTE_AGENT_HOST_CMD'];
		let command: string;
		let args: string[];

		if (cmdEnv) {
			const parts = cmdEnv.split(/\s+/);
			command = parts[0];
			args = parts.slice(1);
		} else {
			command = 'npx';
			args = ['@anthropic-ai/agent-host', '--port', String(this.port)];
		}

		this.outputChannel.appendLine(`[Codamente] Starting agent host: ${command} ${args.join(' ')}`);

		this.process = spawn(command, args, {
			stdio: ['ignore', 'pipe', 'pipe'],
			env: {
				...process.env,
				PORT: String(this.port),
			},
		});

		this.process.stdout?.on('data', (data: Buffer) => {
			this.outputChannel.append(data.toString());
		});

		this.process.stderr?.on('data', (data: Buffer) => {
			this.outputChannel.append(data.toString());
		});

		this.process.on('exit', (code, signal) => {
			this.outputChannel.appendLine(
				`[Codamente] Agent host exited (code=${code}, signal=${signal})`,
			);
			this.process = undefined;

			if (code !== 0 && code !== null) {
				vscode.window.showWarningMessage(
					`Codamente: Agent host exited unexpectedly (code ${code}).`,
				);
			}
		});

		// Wait briefly for the process to start (or fail immediately)
		await new Promise<void>((resolve, reject) => {
			const timeout = setTimeout(() => resolve(), 2000);

			this.process?.on('error', (err) => {
				clearTimeout(timeout);
				this.process = undefined;
				reject(new Error(`Failed to spawn agent host: ${err.message}`));
			});

			// If the process exits within the startup window, treat as failure
			this.process?.on('exit', (code) => {
				if (code !== null && code !== 0) {
					clearTimeout(timeout);
					reject(new Error(`Agent host exited immediately with code ${code}`));
				}
			});
		});
	}

	/** Stop the agent host process. */
	stop(): void {
		if (this.process && this.process.exitCode === null) {
			this.outputChannel.appendLine('[Codamente] Stopping agent host…');
			this.process.kill('SIGTERM');

			// Force kill after a grace period
			setTimeout(() => {
				if (this.process && this.process.exitCode === null) {
					this.process.kill('SIGKILL');
				}
			}, 5000);
		}
		this.process = undefined;
	}
}
