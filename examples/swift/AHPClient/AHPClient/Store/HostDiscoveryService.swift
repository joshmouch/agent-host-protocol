import Foundation

// MARK: - HostDiscoveryService

/// Client for the Codamente host registry server.
///
/// Fetches running agent hosts that have been registered by VS Code instances
/// (via the Codamente extension) and retrieves connection info for connecting.
actor HostDiscoveryService {

    // MARK: - Configuration

    /// Base URL for the Codamente host registry API.
    static let defaultBaseURL = URL(string: "https://codamente.com/api")!

    private let baseURL: URL
    private let session: URLSession

    // MARK: - Init

    init(baseURL: URL = HostDiscoveryService.defaultBaseURL) {
        self.baseURL = baseURL
        self.session = URLSession.shared
    }

    // MARK: - Public API

    /// Fetch all registered hosts for the authenticated user.
    func listHosts(token: String) async throws -> [RemoteHost] {
        let url = baseURL.appendingPathComponent("hosts")
        var request = URLRequest(url: url)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await session.data(for: request)
        try validateResponse(response)

        return try JSONDecoder().decode([RemoteHost].self, from: data)
    }

    /// Get connection info (tunnelUrl + connectionToken) for a specific host.
    func getConnectInfo(hostId: String, token: String) async throws -> HostConnectInfo {
        let url = baseURL.appendingPathComponent("hosts/\(hostId)/connect")
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await session.data(for: request)
        try validateResponse(response)

        return try JSONDecoder().decode(HostConnectInfo.self, from: data)
    }

    /// Check if the registry server is reachable.
    func healthCheck() async throws -> Bool {
        let url = baseURL.appendingPathComponent("health")
        let request = URLRequest(url: url)

        let (_, response) = try await session.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse else { return false }
        return httpResponse.statusCode == 200
    }

    // MARK: - Private

    private func validateResponse(_ response: URLResponse) throws {
        guard let httpResponse = response as? HTTPURLResponse else {
            throw HostDiscoveryError.invalidResponse
        }

        switch httpResponse.statusCode {
        case 200...299:
            return
        case 401:
            throw HostDiscoveryError.unauthorized
        case 404:
            throw HostDiscoveryError.notFound
        default:
            throw HostDiscoveryError.serverError(httpResponse.statusCode)
        }
    }
}

// MARK: - Types

/// A registered agent host from the Codamente registry.
struct RemoteHost: Codable, Identifiable, Sendable {
    let id: String
    let tunnelUrl: String
    let hostName: String
}

/// Connection info returned by the registry for connecting to a host.
struct HostConnectInfo: Codable, Sendable {
    let tunnelUrl: String
    let connectionToken: String
}

// MARK: - Errors

enum HostDiscoveryError: LocalizedError {
    case invalidResponse
    case unauthorized
    case notFound
    case serverError(Int)

    var errorDescription: String? {
        switch self {
        case .invalidResponse: "Invalid response from host registry."
        case .unauthorized: "Authentication failed. Please sign in again."
        case .notFound: "Host not found."
        case .serverError(let code): "Server error (\(code))."
        }
    }
}
