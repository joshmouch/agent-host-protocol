import * as https from 'https';
import * as http from 'http';

/**
 * Client for the Codamente host registry API.
 *
 * Handles host registration, heartbeats, and deregistration.
 * Mirrors the Swift `HostDiscoveryService` from the iOS client.
 */
export class RegistryClient {
	private readonly baseUrl: string;
	private readonly heartbeatIntervalSeconds: number;
	private heartbeatTimer: ReturnType<typeof setInterval> | undefined;

	private _hostId: string | undefined;
	private _isRegistered = false;

	constructor(baseUrl: string, heartbeatIntervalSeconds: number) {
		this.baseUrl = baseUrl.replace(/\/+$/, '');
		this.heartbeatIntervalSeconds = heartbeatIntervalSeconds;
	}

	get hostId(): string | undefined {
		return this._hostId;
	}

	get isRegistered(): boolean {
		return this._isRegistered;
	}

	/**
	 * Register this host with the registry.
	 *
	 * POST /hosts  { tunnelUrl, hostName }
	 */
	async register(tunnelUrl: string, hostName: string, token: string): Promise<void> {
		const body = JSON.stringify({ tunnelUrl, hostName });
		const response = await this.request('POST', '/hosts', token, body);

		if (response.statusCode && response.statusCode >= 200 && response.statusCode < 300) {
			const data = JSON.parse(response.body);
			this._hostId = data.id;
			this._isRegistered = true;
		} else {
			throw new Error(
				`Registry registration failed: HTTP ${response.statusCode} — ${response.body}`,
			);
		}
	}

	/**
	 * Send a heartbeat for the registered host.
	 *
	 * PUT /hosts/:id/heartbeat
	 */
	async heartbeat(token: string): Promise<void> {
		if (!this._hostId) {
			return;
		}

		try {
			await this.request('PUT', `/hosts/${this._hostId}/heartbeat`, token);
		} catch {
			// Heartbeat failures are non-fatal; the host will be cleaned up by the registry
			// after missing enough heartbeats.
		}
	}

	/** Start the periodic heartbeat loop. */
	startHeartbeat(token: string): void {
		this.stopHeartbeat();
		this.heartbeatTimer = setInterval(
			() => void this.heartbeat(token),
			this.heartbeatIntervalSeconds * 1000,
		);
	}

	/** Stop the periodic heartbeat loop. */
	stopHeartbeat(): void {
		if (this.heartbeatTimer) {
			clearInterval(this.heartbeatTimer);
			this.heartbeatTimer = undefined;
		}
	}

	/**
	 * Deregister this host from the registry.
	 *
	 * DELETE /hosts/:id
	 */
	async deregister(token: string): Promise<void> {
		if (!this._hostId) {
			return;
		}

		try {
			await this.request('DELETE', `/hosts/${this._hostId}`, token);
		} catch {
			// Best-effort deregistration
		}

		this._hostId = undefined;
		this._isRegistered = false;
	}

	// ---------- Private ----------

	private request(
		method: string,
		path: string,
		token: string,
		body?: string,
	): Promise<{ statusCode: number | undefined; body: string }> {
		return new Promise((resolve, reject) => {
			const url = new URL(`${this.baseUrl}${path}`);
			const transport = url.protocol === 'https:' ? https : http;

			const req = transport.request(
				url,
				{
					method,
					headers: {
						'Authorization': `Bearer ${token}`,
						'Content-Type': 'application/json',
						'Accept': 'application/json',
						...(body ? { 'Content-Length': Buffer.byteLength(body) } : {}),
					},
				},
				(res) => {
					let data = '';
					res.on('data', (chunk: Buffer) => {
						data += chunk.toString();
					});
					res.on('end', () => {
						resolve({ statusCode: res.statusCode, body: data });
					});
				},
			);

			req.on('error', reject);

			if (body) {
				req.write(body);
			}
			req.end();
		});
	}
}
