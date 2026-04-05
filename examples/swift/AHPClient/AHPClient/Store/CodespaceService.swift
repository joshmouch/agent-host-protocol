import Foundation

// MARK: - CodespaceService

/// Client for the GitHub Codespaces REST API.
///
/// Provisions new Codespaces, polls for readiness, lists existing Codespaces,
/// and manages their lifecycle. Uses the authenticated user's GitHub token.
actor CodespaceService {

    // MARK: - Configuration

    private static let apiBase = URL(string: "https://api.github.com")!

    /// How often to poll the codespace state while provisioning (seconds).
    private static let pollInterval: TimeInterval = 5

    /// Maximum time to wait for a codespace to become available (seconds).
    private static let maxWaitTime: TimeInterval = 300

    private let session: URLSession

    // MARK: - Init

    init() {
        self.session = URLSession.shared
    }

    // MARK: - Public API

    /// Search repositories the user has access to.
    func searchRepositories(query: String, token: String) async throws -> [GitHubRepository] {
        var components = URLComponents(url: Self.apiBase.appendingPathComponent("search/repositories"), resolvingAgainstBaseURL: false)!
        components.queryItems = [
            URLQueryItem(name: "q", value: "\(query) in:name"),
            URLQueryItem(name: "sort", value: "updated"),
            URLQueryItem(name: "per_page", value: "20"),
        ]

        var request = URLRequest(url: components.url!)
        request.applyGitHubHeaders(token: token)

        let (data, response) = try await session.data(for: request)
        try validateResponse(response)

        let result = try JSONDecoder().decode(RepositorySearchResult.self, from: data)
        return result.items
    }

    /// List the user's repositories.
    func listUserRepos(token: String, page: Int = 1) async throws -> [GitHubRepository] {
        var components = URLComponents(url: Self.apiBase.appendingPathComponent("user/repos"), resolvingAgainstBaseURL: false)!
        components.queryItems = [
            URLQueryItem(name: "sort", value: "updated"),
            URLQueryItem(name: "per_page", value: "30"),
            URLQueryItem(name: "page", value: "\(page)"),
        ]

        var request = URLRequest(url: components.url!)
        request.applyGitHubHeaders(token: token)

        let (data, response) = try await session.data(for: request)
        try validateResponse(response)

        return try JSONDecoder().decode([GitHubRepository].self, from: data)
    }

    /// Create a new Codespace for a repository.
    func createCodespace(
        owner: String,
        repo: String,
        ref: String = "main",
        token: String
    ) async throws -> Codespace {
        let url = Self.apiBase.appendingPathComponent("repos/\(owner)/\(repo)/codespaces")
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.applyGitHubHeaders(token: token)
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        let body: [String: Any] = [
            "ref": ref,
            "idle_timeout_minutes": 30,
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response) = try await session.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw CodespaceError.invalidResponse
        }
        guard (200...299).contains(httpResponse.statusCode) else {
            let errorBody = String(data: data, encoding: .utf8) ?? "Unknown error"
            throw CodespaceError.creationFailed("HTTP \(httpResponse.statusCode): \(errorBody)")
        }

        return try JSONDecoder().decode(Codespace.self, from: data)
    }

    /// Get the current state of a Codespace.
    func getCodespace(name: String, token: String) async throws -> Codespace {
        let url = Self.apiBase.appendingPathComponent("user/codespaces/\(name)")
        var request = URLRequest(url: url)
        request.applyGitHubHeaders(token: token)

        let (data, response) = try await session.data(for: request)
        try validateResponse(response)

        return try JSONDecoder().decode(Codespace.self, from: data)
    }

    /// Poll a Codespace until it reaches "Available" state.
    /// Returns the final Codespace or throws if timeout is exceeded.
    func waitForCodespace(name: String, token: String) async throws -> Codespace {
        let deadline = Date().addingTimeInterval(Self.maxWaitTime)

        while Date() < deadline {
            let codespace = try await getCodespace(name: name, token: token)

            switch codespace.state {
            case "Available":
                return codespace
            case "Queued", "Provisioning", "Created", "Starting", "Awaiting":
                try await Task.sleep(nanoseconds: UInt64(Self.pollInterval * 1_000_000_000))
            default:
                throw CodespaceError.unexpectedState(codespace.state)
            }

            try Task.checkCancellation()
        }

        throw CodespaceError.timeout
    }

    /// List the user's Codespaces.
    func listCodespaces(token: String) async throws -> [Codespace] {
        let url = Self.apiBase.appendingPathComponent("user/codespaces")
        var request = URLRequest(url: url)
        request.applyGitHubHeaders(token: token)

        let (data, response) = try await session.data(for: request)
        try validateResponse(response)

        let result = try JSONDecoder().decode(CodespaceListResult.self, from: data)
        return result.codespaces
    }

    /// Stop a running Codespace.
    func stopCodespace(name: String, token: String) async throws {
        let url = Self.apiBase.appendingPathComponent("user/codespaces/\(name)/stop")
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.applyGitHubHeaders(token: token)

        let (_, response) = try await session.data(for: request)
        try validateResponse(response)
    }

    /// Delete a Codespace.
    func deleteCodespace(name: String, token: String) async throws {
        let url = Self.apiBase.appendingPathComponent("user/codespaces/\(name)")
        var request = URLRequest(url: url)
        request.httpMethod = "DELETE"
        request.applyGitHubHeaders(token: token)

        let (_, response) = try await session.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse,
              (200...299).contains(httpResponse.statusCode) || httpResponse.statusCode == 304 else {
            throw CodespaceError.invalidResponse
        }
    }

    // MARK: - Private

    private func validateResponse(_ response: URLResponse) throws {
        guard let httpResponse = response as? HTTPURLResponse else {
            throw CodespaceError.invalidResponse
        }

        switch httpResponse.statusCode {
        case 200...299:
            return
        case 401:
            throw CodespaceError.unauthorized
        case 403:
            throw CodespaceError.forbidden
        case 404:
            throw CodespaceError.notFound
        default:
            throw CodespaceError.serverError(httpResponse.statusCode)
        }
    }
}

// MARK: - URLRequest Extension

private extension URLRequest {
    mutating func applyGitHubHeaders(token: String) {
        setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
    }
}

// MARK: - Types

/// A GitHub repository from the API.
struct GitHubRepository: Codable, Identifiable, Sendable {
    let id: Int
    let name: String
    let fullName: String
    let owner: RepositoryOwner
    let description: String?
    let defaultBranch: String
    let isPrivate: Bool
    let language: String?
    let updatedAt: String?

    enum CodingKeys: String, CodingKey {
        case id, name, owner, description, language
        case fullName = "full_name"
        case defaultBranch = "default_branch"
        case isPrivate = "private"
        case updatedAt = "updated_at"
    }
}

struct RepositoryOwner: Codable, Sendable {
    let login: String
    let avatarUrl: String?

    enum CodingKeys: String, CodingKey {
        case login
        case avatarUrl = "avatar_url"
    }
}

/// A GitHub Codespace from the API.
struct Codespace: Codable, Identifiable, Sendable {
    let id: Int
    let name: String
    let state: String
    let repository: CodespaceRepository
    let createdAt: String?
    let webUrl: String?

    /// Default port used by the agent host inside Codespaces.
    private static let agentHostPort = 8081

    /// The public URL pattern for forwarded ports.
    var portForwardingURL: String {
        "https://\(name)-\(Self.agentHostPort).app.github.dev"
    }

    enum CodingKeys: String, CodingKey {
        case id, name, state, repository
        case createdAt = "created_at"
        case webUrl = "web_url"
    }
}

struct CodespaceRepository: Codable, Sendable {
    let id: Int
    let fullName: String

    enum CodingKeys: String, CodingKey {
        case id
        case fullName = "full_name"
    }
}

/// Search result wrapper.
private struct RepositorySearchResult: Codable {
    let items: [GitHubRepository]
}

/// Codespace list result wrapper.
private struct CodespaceListResult: Codable {
    let codespaces: [Codespace]
}

// MARK: - Errors

enum CodespaceError: LocalizedError {
    case invalidResponse
    case unauthorized
    case forbidden
    case notFound
    case creationFailed(String)
    case unexpectedState(String)
    case timeout
    case serverError(Int)

    var errorDescription: String? {
        switch self {
        case .invalidResponse: "Invalid response from GitHub API."
        case .unauthorized: "Authentication failed. Please sign in again."
        case .forbidden: "You don't have permission to perform this action."
        case .notFound: "Resource not found."
        case .creationFailed(let detail): "Failed to create Codespace: \(detail)"
        case .unexpectedState(let state): "Codespace entered unexpected state: \(state)"
        case .timeout: "Timed out waiting for Codespace to become available."
        case .serverError(let code): "GitHub API error (\(code))."
        }
    }
}
