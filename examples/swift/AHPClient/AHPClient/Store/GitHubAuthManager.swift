import AuthenticationServices
import Foundation
import Security

// MARK: - GitHubAuthManager

/// Manages GitHub OAuth authentication and secure token storage via iOS Keychain.
///
/// Uses `ASWebAuthenticationSession` for the OAuth web flow. The token is persisted in
/// the Keychain so the user stays signed in across app launches.
@Observable
@MainActor
final class GitHubAuthManager: NSObject {

    // MARK: - Configuration

    /// GitHub OAuth app client ID.
    /// Replace with your own GitHub OAuth App's client ID.
    static let clientId = "GITHUB_CLIENT_ID"

    /// OAuth callback URL scheme registered in Info.plist.
    static let callbackScheme = "ahpclient"

    /// Scopes required for host registry + codespace provisioning.
    static let scopes = "read:user codespace repo"

    // MARK: - Published State

    /// The current GitHub access token, if authenticated.
    private(set) var token: String?

    /// The authenticated GitHub user info.
    private(set) var user: GitHubUser?

    /// Whether authentication is in progress.
    private(set) var isAuthenticating = false

    /// Last authentication error message.
    var authError: String?

    /// Whether the user is signed in.
    var isSignedIn: Bool { token != nil }

    // MARK: - Keychain

    private static let keychainService = "com.ahpclient.github"
    private static let keychainTokenKey = "github_token"

    // MARK: - Init

    override init() {
        super.init()
        // Restore token from Keychain on launch
        token = Self.loadTokenFromKeychain()
        if token != nil {
            Task { await fetchUser() }
        }
    }

    // MARK: - Sign In

    /// Start the GitHub OAuth web flow.
    func signIn() async {
        guard !isAuthenticating else { return }
        isAuthenticating = true
        authError = nil

        defer { isAuthenticating = false }

        do {
            let code = try await requestAuthorizationCode()
            let accessToken = try await exchangeCodeForToken(code)
            Self.saveTokenToKeychain(accessToken)
            token = accessToken
            await fetchUser()
        } catch {
            if (error as NSError).domain == ASWebAuthenticationSessionError.errorDomain,
               (error as NSError).code == ASWebAuthenticationSessionError.canceledLogin.rawValue {
                // User cancelled — not an error
                return
            }
            authError = error.localizedDescription
        }
    }

    /// Sign out and clear the token.
    func signOut() {
        Self.deleteTokenFromKeychain()
        token = nil
        user = nil
    }

    // MARK: - GitHub User

    /// Fetch the authenticated user's profile from GitHub.
    func fetchUser() async {
        guard let token else { return }

        var request = URLRequest(url: URL(string: "https://api.github.com/user")!)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")

        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let httpResponse = response as? HTTPURLResponse,
                  httpResponse.statusCode == 200 else {
                // Token might be expired — sign out
                signOut()
                return
            }
            user = try JSONDecoder().decode(GitHubUser.self, from: data)
        } catch {
            // Non-fatal
            print("[Auth] Failed to fetch user: \(error)")
        }
    }

    // MARK: - Private: OAuth Flow

    private func requestAuthorizationCode() async throws -> String {
        var components = URLComponents(string: "https://github.com/login/oauth/authorize")!
        components.queryItems = [
            URLQueryItem(name: "client_id", value: Self.clientId),
            URLQueryItem(name: "scope", value: Self.scopes),
            URLQueryItem(name: "redirect_uri", value: "\(Self.callbackScheme)://oauth/callback"),
        ]

        let url = components.url!
        let callbackScheme = Self.callbackScheme

        return try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(
                url: url,
                callbackURLScheme: callbackScheme
            ) { callbackURL, error in
                if let error {
                    continuation.resume(throwing: error)
                    return
                }

                guard let callbackURL,
                      let components = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false),
                      let code = components.queryItems?.first(where: { $0.name == "code" })?.value else {
                    continuation.resume(throwing: AuthError.noCodeInCallback)
                    return
                }

                continuation.resume(returning: code)
            }

            session.presentationContextProvider = self
            session.prefersEphemeralWebBrowserSession = false
            session.start()
        }
    }

    /// Exchange the authorization code for an access token.
    ///
    /// > Important: In production, this exchange should happen on a backend server
    /// > that holds the OAuth client secret. For this example client, the exchange
    /// > is done directly.
    private func exchangeCodeForToken(_ code: String) async throws -> String {
        var request = URLRequest(url: URL(string: "https://github.com/login/oauth/access_token")!)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let body: [String: String] = [
            "client_id": Self.clientId,
            "code": code,
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse,
              httpResponse.statusCode == 200 else {
            throw AuthError.tokenExchangeFailed
        }

        let result = try JSONDecoder().decode(TokenResponse.self, from: data)
        guard let accessToken = result.accessToken, !accessToken.isEmpty else {
            throw AuthError.tokenExchangeFailed
        }

        return accessToken
    }

    // MARK: - Private: Keychain

    private static func saveTokenToKeychain(_ token: String) {
        deleteTokenFromKeychain()

        let data = Data(token.utf8)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainTokenKey,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
        ]

        SecItemAdd(query as CFDictionary, nil)
    }

    private static func loadTokenFromKeychain() -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainTokenKey,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)

        guard status == errSecSuccess, let data = result as? Data else {
            return nil
        }

        return String(data: data, encoding: .utf8)
    }

    private static func deleteTokenFromKeychain() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: keychainTokenKey,
        ]

        SecItemDelete(query as CFDictionary)
    }
}

// MARK: - ASWebAuthenticationPresentationContextProviding

extension GitHubAuthManager: ASWebAuthenticationPresentationContextProviding {
    nonisolated func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        ASPresentationAnchor()
    }
}

// MARK: - Types

enum AuthError: LocalizedError {
    case noCodeInCallback
    case tokenExchangeFailed

    var errorDescription: String? {
        switch self {
        case .noCodeInCallback: "No authorization code received from GitHub."
        case .tokenExchangeFailed: "Failed to exchange code for access token."
        }
    }
}

/// Minimal GitHub user representation.
struct GitHubUser: Codable, Sendable {
    let id: Int
    let login: String
    let avatarUrl: String?

    enum CodingKeys: String, CodingKey {
        case id, login
        case avatarUrl = "avatar_url"
    }
}

/// Token exchange response from GitHub OAuth.
private struct TokenResponse: Codable {
    let accessToken: String?
    let tokenType: String?
    let scope: String?

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case tokenType = "token_type"
        case scope
    }
}
