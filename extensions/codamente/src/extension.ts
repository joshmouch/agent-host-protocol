import * as vscode from 'vscode';
import { AgentHostManager } from './agentHostManager';
import { RegistryClient } from './registryClient';

let agentHostManager: AgentHostManager | undefined;
let registryClient: RegistryClient | undefined;
let statusBarItem: vscode.StatusBarItem;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
	const config = vscode.workspace.getConfiguration('codamente');
	const registryUrl = config.get<string>('registryUrl', 'https://codamente.com/api');
	const agentHostPort = config.get<number>('agentHostPort', 8081);
	const autoStart = config.get<boolean>('autoStartInCodespaces', true);
	const heartbeatInterval = config.get<number>('heartbeatIntervalSeconds', 30);

	// Status bar
	statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
	statusBarItem.command = 'codamente.showStatus';
	context.subscriptions.push(statusBarItem);

	// Services
	agentHostManager = new AgentHostManager(agentHostPort);
	registryClient = new RegistryClient(registryUrl, heartbeatInterval);

	// Commands
	context.subscriptions.push(
		vscode.commands.registerCommand('codamente.startAgentHost', async () => {
			await startAgentHost(context, agentHostPort);
		}),
		vscode.commands.registerCommand('codamente.stopAgentHost', async () => {
			await stopAgentHost();
		}),
		vscode.commands.registerCommand('codamente.showStatus', () => {
			showStatus();
		}),
	);

	// Auto-start in Codespaces
	const codespaceName = process.env['CODESPACE_NAME'];
	if (codespaceName && autoStart) {
		vscode.window.showInformationMessage(
			`Codamente: Codespace detected (${codespaceName}). Starting agent host…`,
		);
		await startAgentHost(context, agentHostPort);
	}

	updateStatusBar();
}

export async function deactivate(): Promise<void> {
	await stopAgentHost();
}

async function startAgentHost(
	context: vscode.ExtensionContext,
	port: number,
): Promise<void> {
	if (!agentHostManager || !registryClient) {
		return;
	}

	if (agentHostManager.isRunning) {
		vscode.window.showInformationMessage('Codamente: Agent host is already running.');
		return;
	}

	try {
		await agentHostManager.start();

		// Determine the public URL for registry
		const tunnelUrl = buildTunnelUrl(port);

		// Get GitHub token for registry auth
		const session = await vscode.authentication.getSession('github', ['read:user'], {
			createIfNone: false,
		});

		if (session) {
			const hostName = buildHostName();
			await registryClient.register(tunnelUrl, hostName, session.accessToken);
			registryClient.startHeartbeat(session.accessToken);
		} else {
			vscode.window.showWarningMessage(
				'Codamente: No GitHub session available. Agent host started but not registered with the registry.',
			);
		}

		vscode.window.showInformationMessage('Codamente: Agent host started.');
	} catch (error) {
		const message = error instanceof Error ? error.message : String(error);
		vscode.window.showErrorMessage(`Codamente: Failed to start agent host — ${message}`);
	}

	updateStatusBar();
}

async function stopAgentHost(): Promise<void> {
	if (registryClient) {
		try {
			const session = await vscode.authentication.getSession('github', ['read:user'], {
				createIfNone: false,
			});
			if (session) {
				await registryClient.deregister(session.accessToken);
			}
		} catch {
			// Best-effort deregistration on shutdown
		}
		registryClient.stopHeartbeat();
	}

	if (agentHostManager) {
		agentHostManager.stop();
	}

	updateStatusBar();
}

/** Build the public tunnel URL for the agent host port in a Codespace. */
function buildTunnelUrl(port: number): string {
	const codespaceName = process.env['CODESPACE_NAME'];
	const domain = process.env['GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN'] ?? 'app.github.dev';
	if (codespaceName) {
		return `https://${codespaceName}-${port}.${domain}`;
	}
	// Fallback for non-Codespace (e.g. local dev tunnel)
	return `http://localhost:${port}`;
}

/** Build a human-readable host name. */
function buildHostName(): string {
	const codespaceName = process.env['CODESPACE_NAME'];
	if (codespaceName) {
		return codespaceName;
	}
	const hostname = require('os').hostname();
	return `vscode-${hostname}`;
}

function showStatus(): void {
	const running = agentHostManager?.isRunning ?? false;
	const registered = registryClient?.isRegistered ?? false;
	const hostId = registryClient?.hostId;

	const lines = [
		`Agent Host: ${running ? '✅ Running' : '⏹ Stopped'}`,
		`Registry: ${registered ? `✅ Registered (${hostId})` : '⏹ Not registered'}`,
		`Codespace: ${process.env['CODESPACE_NAME'] ?? 'N/A'}`,
	];

	vscode.window.showInformationMessage(lines.join('\n'));
}

function updateStatusBar(): void {
	const running = agentHostManager?.isRunning ?? false;
	if (running) {
		statusBarItem.text = '$(broadcast) AHP Host';
		statusBarItem.tooltip = 'Agent Host Protocol — Running';
		statusBarItem.show();
	} else {
		statusBarItem.hide();
	}
}
