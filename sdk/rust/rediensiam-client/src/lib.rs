//! Resource-server client for [RediensIAM].
//!
//! Validates bearer tokens by asking RediensIAM, not by checking a signature locally. A valid
//! signature only proves a token was issued; it cannot see a role revoked, a service account
//! disabled, or an organisation suspended after issuance.
//!
//! ```no_run
//! use rediensiam_client::{Config, RediensIamClient};
//!
//! # async fn example() -> Result<(), Box<dyn std::error::Error>> {
//! let iam = RediensIamClient::new(Config {
//!     base_url: "https://auth.example.com".into(),
//!     service_account_token: std::env::var("REDIENSIAM_TOKEN")?,
//!     // The tenant this service serves. Required — see [`Config::project_id`].
//!     project_id: "<project-or-org-id>".into(),
//!     ..Default::default()
//! })?;
//!
//! let info = iam.introspect("rediens_pat_...").await?;
//! // Tenant roles are namespaced by project — "admin" alone is not a cross-tenant identity.
//! if info.active && info.has_project_role("<project-id>", "admin") {
//!     // proceed
//! }
//! # Ok(())
//! # }
//! ```
//!
//! # What this client trusts
//!
//! The TLS root store is the CA bundle compiled into the binary **plus** the host's own trust
//! store — `/etc/ssl/certs` (or `SSL_CERT_FILE` / `SSL_CERT_DIR`) on Unix, the Keychain on macOS,
//! the certificate store on Windows. A deployment behind a private CA works by installing that CA
//! on the host, the same act that makes `curl` work, with nothing to configure here.
//!
//! Roots are only ever added: verification itself is unchanged, and a certificate from a CA the
//! host does not trust is still refused. The cost is that the host store is now load-bearing —
//! anything an operator adds there, this client will accept — the same exposure the .NET and
//! browser SDKs have always had, and the same one `curl` has.
//!
//! [RediensIAM]: https://github.com/rediens/rediensiam

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tokio::sync::RwLock;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("transport error talking to RediensIAM: {0}")]
    Transport(#[from] reqwest::Error),

    #[error("RediensIAM returned {status}: {body}")]
    Api { status: u16, body: String },

    #[error("invalid configuration: {0}")]
    Config(String),
}

/// Answer to an introspection call.
///
/// `active == false` means the token is not usable right now — expired, revoked, belonging to a
/// deactivated service account, or to a suspended organisation. The reason is deliberately not
/// disclosed by the server.
#[derive(Debug, Clone, Default, Deserialize)]
pub struct TokenInfo {
    pub active: bool,
    #[serde(default)]
    pub sub: Option<String>,
    #[serde(default)]
    pub user_id: Option<String>,
    #[serde(default)]
    pub org_id: Option<String>,
    #[serde(default)]
    pub project_id: Option<String>,
    /// Roles the token carries. Empty rather than absent when the token is inactive.
    ///
    /// `deserialize_with` and not a bare `default`: the latter fills a *missing* field but errors
    /// on an explicit `null`, and an inactive answer from a server older than the fix sends
    /// `"roles": null`. That turned every expired or revoked token into `Error::Transport`, which
    /// this crate documents as "the IAM is unreachable" — the opposite of what had happened.
    #[serde(default, deserialize_with = "null_as_empty")]
    pub roles: Vec<String>,
    #[serde(default)]
    pub client_id: Option<String>,
    #[serde(default)]
    pub is_service_account: bool,

    /// Who is acting for whom. `None` on every ordinary token — `Some` means an operator opened a
    /// delegated session into this tenant, and the request in front of you is support traffic
    /// rather than the customer's own.
    ///
    /// A delegated token carries **no roles**: authority still comes from your own enforcement
    /// point. What this decides is what you show, what you record, and — while `mode` reads
    /// `"read"` — what you refuse.
    #[serde(default)]
    pub act: Option<Actor>,
}

/// The operator behind a delegated session (RFC 8693 `act`).
///
/// `mode` is a claim, and a claim enforces nothing on its own — refusing mutating verbs while it
/// reads `"read"` is your gateway's job. `session` is what revokes it, and what your logs should
/// carry beside the tenant id so a support action is never indistinguishable from the customer's.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Actor {
    #[serde(default)]
    pub sub: String,
    #[serde(default)]
    pub level: String,
    #[serde(default)]
    pub mode: String,
    #[serde(default)]
    pub session: String,
}

impl TokenInfo {
    /// An inactive answer. Returned for empty tokens without a round-trip.
    pub fn inactive() -> Self {
        Self::default()
    }

    /// True when this request is an operator acting for the tenant, in read-only mode.
    pub fn is_read_only_impersonation(&self) -> bool {
        self.act.as_ref().is_some_and(|a| a.mode == "read")
    }

    /// True when the token carries a **management** role of RediensIAM itself
    /// (`super_admin`, `org_admin`, `project_admin`). Tenant roles never match here: the issuer
    /// namespaces them by project, so use [`TokenInfo::has_project_role`] for those.
    pub fn has_role(&self, role: &str) -> bool {
        self.roles.iter().any(|r| r == role)
    }

    /// True when the token carries tenant role `role` **in project `project_id`**.
    ///
    /// Role names are chosen by each tenant, so `"admin"` on its own means nothing across
    /// tenants — the issuer emits them as `{project_id}/{name}` and this is the matching read.
    pub fn has_project_role(&self, project_id: &str, role: &str) -> bool {
        let qualified = format!("{project_id}/{role}");
        self.roles.contains(&qualified)
    }

    /// True when the token belongs to `org_id` — the check a multi-tenant resource server needs
    /// before serving any tenant-scoped data.
    pub fn belongs_to_org(&self, org_id: &str) -> bool {
        self.org_id.as_deref() == Some(org_id)
    }
}

#[derive(Debug, Clone)]
pub struct Config {
    /// Base URL of the RediensIAM public API, e.g. `https://auth.example.com`.
    ///
    /// Must be `https`. The one exception is a loopback host (`localhost`, `127.0.0.1`, `::1`),
    /// so a local development setup does not have to switch the check off everywhere.
    pub base_url: String,

    /// Credential this service presents. A service-account personal access token
    /// (`rediens_pat_…`) is the simplest option.
    pub service_account_token: String,

    /// The tenant **this resource server serves** — the project id it fronts, or the organisation
    /// id if it fronts a whole organisation. Sent as `project_id` on every introspect and authorize
    /// call, and required by the server.
    ///
    /// This is the same identifier that gives the front `client_<project_id>`, so one value
    /// configures both halves of an integration.
    ///
    /// Required, with deliberately no default. A default would be a guess about which tenant this
    /// service belongs to, and a wrong guess is P-06 exactly: a deployment-scoped service-account
    /// credential resolving *every* tenant's token as `active: true`, leaving the resource server
    /// to remember a `project_id` comparison nobody remembers.
    pub project_id: String,

    /// How long a positive introspection is reused. This is the upper bound on how long a
    /// revoked token keeps working here, so keep it short. `Duration::ZERO` disables caching.
    pub cache_duration: Duration,

    pub timeout: Duration,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            base_url: String::new(),
            service_account_token: String::new(),
            project_id: String::new(),
            cache_duration: Duration::from_secs(30),
            timeout: Duration::from_secs(5),
        }
    }
}

#[derive(Serialize)]
struct AuthorizeRequest<'a> {
    token: &'a str,
    namespace: &'a str,
    object: &'a str,
    relation: &'a str,
    project_id: &'a str,
}

#[derive(Serialize)]
struct OpenImpersonationRequest<'a> {
    org_id: &'a str,
    project_id: &'a str,
    mode: &'a str,
    reason: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    ttl_seconds: Option<u32>,
}

/// A delegated session, as returned when it is opened. `access_token` is shown once.
#[derive(Debug, Clone, Default, Deserialize)]
pub struct ImpersonationSession {
    pub access_token: String,
    pub session_id: String,
    pub expires_in: u32,
    #[serde(default)]
    pub sub: String,
    #[serde(default)]
    pub org_id: String,
    #[serde(default)]
    pub project_id: String,
    #[serde(default)]
    pub act: Option<Actor>,
}

#[derive(Deserialize)]
struct AuthorizeResponse {
    allowed: bool,
}

struct CacheEntry {
    info: TokenInfo,
    expires_at: Instant,
}

/// Client for RediensIAM's resource-server surface. Cheap to clone — the inner state is shared.
#[derive(Clone)]
pub struct RediensIamClient {
    http: reqwest::Client,
    config: Arc<Config>,
    // ponytail: one RwLock over the whole map. Swap for a sharded/LRU cache only if contention
    // shows up in a profile — entries are tiny and expire within seconds.
    cache: Arc<RwLock<HashMap<String, CacheEntry>>>,
}

impl RediensIamClient {
    pub fn new(config: Config) -> Result<Self, Error> {
        if config.base_url.is_empty() {
            return Err(Error::Config("base_url is required".into()));
        }
        if config.service_account_token.is_empty() {
            return Err(Error::Config("service_account_token is required".into()));
        }
        // Checked here rather than on the first call, for the same reason the https check is:
        // a resource server with no declared tenant is a deployment mistake, and it should stop
        // the process at startup instead of returning 400s under load.
        if config.project_id.is_empty() {
            return Err(Error::Config(
                "project_id is required: name the project id this resource server serves, or its \
                 organisation id if it fronts a whole organisation. RediensIAM sends it as `project_id` \
                 and refuses a request without one (400 project_id_required)."
                    .into(),
            ));
        }
        require_secure_url(&config.base_url)?;

        let http = reqwest::Client::builder().timeout(config.timeout).build()?;

        Ok(Self {
            http,
            config: Arc::new(config),
            cache: Arc::new(RwLock::new(HashMap::new())),
        })
    }

    /// Introspects a token (RFC 7662).
    ///
    /// An unusable token yields `Ok(TokenInfo { active: false, .. })`. Transport and server
    /// faults return `Err` rather than a negative answer, so an IAM outage cannot be mistaken
    /// for "everyone is unauthenticated" — the caller decides how to handle it.
    pub async fn introspect(&self, token: &str) -> Result<TokenInfo, Error> {
        if token.is_empty() {
            return Ok(TokenInfo::inactive());
        }

        let key = cache_key(token);
        if !self.config.cache_duration.is_zero() {
            let cache = self.cache.read().await;
            if let Some(entry) = cache.get(&key) {
                if entry.expires_at > Instant::now() {
                    return Ok(entry.info.clone());
                }
            }
        }

        let url = format!(
            "{}/api/introspect",
            self.config.base_url.trim_end_matches('/')
        );
        let response = self
            .http
            .post(url)
            .bearer_auth(&self.config.service_account_token)
            .form(&[
                ("token", token),
                ("token_type_hint", "access_token"),
                ("project_id", self.config.project_id.as_str()),
            ])
            .send()
            .await?;

        let info: TokenInfo = self.parse(response).await?;

        // Only positive answers are cached: caching "inactive" would keep denying a token that
        // has since become valid, and buys nothing.
        if info.active && !self.config.cache_duration.is_zero() {
            let mut cache = self.cache.write().await;
            // Opportunistic sweep — keeps the map bounded without a background task.
            let now = Instant::now();
            cache.retain(|_, e| e.expires_at > now);
            cache.insert(
                key,
                CacheEntry {
                    info: info.clone(),
                    expires_at: now + self.config.cache_duration,
                },
            );
        }

        Ok(info)
    }

    /// Asks RediensIAM whether the bearer of `token` holds `relation` on the given object.
    /// Keeps the policy in RediensIAM instead of reimplementing an interpretation of the roles
    /// claim in every gateway.
    pub async fn authorize(
        &self,
        token: &str,
        namespace: &str,
        object: &str,
        relation: &str,
    ) -> Result<bool, Error> {
        let url = format!(
            "{}/api/authorize",
            self.config.base_url.trim_end_matches('/')
        );
        let response = self
            .http
            .post(url)
            .bearer_auth(&self.config.service_account_token)
            .json(&AuthorizeRequest {
                token,
                namespace,
                object,
                relation,
                project_id: &self.config.project_id,
            })
            .send()
            .await?;

        let parsed: AuthorizeResponse = self.parse(response).await?;
        Ok(parsed.allowed)
    }

    /// Opens a delegated session — an operator acting **for** the organisation named here.
    ///
    /// Requires a service-account credential that also holds `super_admin`; anything less is
    /// refused by the server. This is an operator console's call, never a customer-facing one.
    ///
    /// The returned `access_token` is shown **once**. Opening a session revokes the same
    /// operator's previous one.
    ///
    /// Sessions are organisation-scoped: no user is impersonated and the token carries no roles,
    /// so what a support session may see is decided by your own service. `reason` is required —
    /// it lands in the entered tenant's audit log, and a session with no stated reason is not
    /// auditable.
    ///
    /// `ttl_seconds` defaults to 900 server-side; anything above 3600 is clamped, not refused.
    pub async fn open_impersonation(
        &self,
        org_id: &str,
        project_id: &str,
        mode: &str,
        reason: &str,
        ttl_seconds: Option<u32>,
    ) -> Result<ImpersonationSession, Error> {
        if reason.trim().is_empty() {
            return Err(Error::Config(
                "a delegated session must state a reason: it is written to the tenant's audit log"
                    .into(),
            ));
        }

        let url = format!(
            "{}/api/manage/impersonate",
            self.config.base_url.trim_end_matches('/')
        );
        let response = self
            .http
            .post(url)
            .bearer_auth(&self.config.service_account_token)
            .json(&OpenImpersonationRequest {
                org_id,
                project_id,
                mode,
                reason,
                ttl_seconds,
            })
            .send()
            .await?;

        self.parse(response).await
    }

    /// Ends a delegated session immediately — not at its TTL. `false` means there was nothing live
    /// to end, which is also what a second call answers.
    pub async fn revoke_impersonation(&self, session_id: &str) -> Result<bool, Error> {
        let url = format!(
            "{}/api/manage/impersonate/{session_id}/revoke",
            self.config.base_url.trim_end_matches('/')
        );
        let response = self
            .http
            .post(url)
            .bearer_auth(&self.config.service_account_token)
            .send()
            .await?;

        if response.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(false);
        }
        let status = response.status();
        if !status.is_success() {
            let body = response.text().await.unwrap_or_default();
            return Err(Error::Api {
                status: status.as_u16(),
                body,
            });
        }
        Ok(true)
    }

    /// Drops any cached decision for a token — call on logout to make it immediate.
    pub async fn forget(&self, token: &str) {
        self.cache.write().await.remove(&cache_key(token));
    }

    async fn parse<T: for<'de> Deserialize<'de>>(
        &self,
        response: reqwest::Response,
    ) -> Result<T, Error> {
        let status = response.status();
        if !status.is_success() {
            let body = response.text().await.unwrap_or_default();
            return Err(Error::Api {
                status: status.as_u16(),
                body,
            });
        }
        Ok(response.json().await?)
    }
}

/// The service-account credential and every token being introspected ride on `base_url`, so
/// cleartext there hands an on-path attacker both. `http` is accepted only on a loopback host:
/// forbidding it outright breaks every local setup, and a flag to disable the check gets set in
/// production too.
fn require_secure_url(base_url: &str) -> Result<(), Error> {
    let url = reqwest::Url::parse(base_url)
        .map_err(|e| Error::Config(format!("base_url is not a valid URL: {e}")))?;

    match url.scheme() {
        "https" => Ok(()),
        // `[::1]` and not `::1`: the url crate keeps the brackets an IPv6 authority is written
        // with, so matching the bare form rejected every IPv6 loopback address — the one form of
        // local development this check was written to allow.
        "http"
            if matches!(
                url.host_str(),
                Some("localhost" | "127.0.0.1" | "[::1]" | "::1")
            ) =>
        {
            Ok(())
        }
        _ => Err(Error::Config(format!(
            "base_url must be https — http is accepted only on localhost: {base_url}"
        ))),
    }
}

/// Cache on a digest, never the token itself: keys end up in dumps and diagnostics.
///
/// It has to be a cryptographic digest. The map it keys returns a full `TokenInfo` — roles
/// included — before any server call, so anything that collides with a cached privileged token
/// is authenticated as that token. A 64-bit non-cryptographic hash (this used FNV-1a) is
/// trivially preimageable once a key is observed in a log or a dump.
fn cache_key(token: &str) -> String {
    let digest = Sha256::digest(token.as_bytes());
    let mut out = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use std::fmt::Write;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

/// Treats `null` as an empty sequence. Used for fields a server may send explicitly null.
fn null_as_empty<'de, D>(deserializer: D) -> Result<Vec<String>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    Ok(Option::<Vec<String>>::deserialize(deserializer)?.unwrap_or_default())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A usable configuration, for tests that vary one field of it.
    fn config(base_url: &str) -> Config {
        Config {
            base_url: base_url.into(),
            service_account_token: "rediens_pat_x".into(),
            project_id: "proj-1".into(),
            ..Default::default()
        }
    }

    #[test]
    fn requires_base_url_and_token() {
        assert!(RediensIamClient::new(Config::default()).is_err());

        assert!(RediensIamClient::new(Config {
            base_url: "https://auth.example.com".into(),
            ..Default::default()
        })
        .is_err());
    }

    /// P-06: a resource server that declares no tenant is refused by the server with
    /// `400 project_id_required`. Refusing at construction turns that into a startup failure with a
    /// message naming the fix, instead of a runtime 400 on every request once traffic arrives.
    #[test]
    fn project_id_is_required_at_construction() {
        // `let Err(..) else` rather than `expect_err`: the client deliberately has no `Debug`,
        // which would print the service-account token.
        let Err(err) = RediensIamClient::new(Config {
            project_id: String::new(),
            ..config("https://auth.example.com")
        }) else {
            panic!("a client with no project_id must not be constructible");
        };

        assert!(matches!(err, Error::Config(_)));
        assert!(err.to_string().contains("project_id"), "{err}");

        assert!(RediensIamClient::new(config("https://auth.example.com")).is_ok());
    }

    /// R-30: the credential rides on every call, so the transport has to be authenticated.
    #[test]
    fn base_url_must_be_https_except_on_loopback() {
        let with = |base_url: &str| RediensIamClient::new(config(base_url));

        assert!(with("https://auth.example.com").is_ok());
        assert!(with("http://localhost:8080").is_ok());
        assert!(with("http://127.0.0.1:8080").is_ok());
        // The url crate keeps the brackets, so this asserts the bracketed form the parser produces
        // rather than the bare address a reader would write in a match arm. It failed before.
        assert!(
            with("http://[::1]:8080").is_ok(),
            "IPv6 loopback is loopback"
        );

        assert!(with("http://auth.example.com").is_err());
        assert!(with("auth.example.com").is_err(), "must be an absolute URL");
    }

    #[test]
    fn cache_key_is_not_the_token() {
        let token = "rediens_pat_supersecret";
        let key = cache_key(token);
        assert!(!key.contains("supersecret"));
        assert_eq!(key, cache_key(token), "must be stable");
        assert_ne!(key, cache_key("rediens_pat_other"));
    }

    /// R-28: the cache key must be a cryptographic digest. A 64-bit non-cryptographic hash
    /// (the previous FNV-1a) let anyone who observed a key construct a preimage and be served
    /// the cached `TokenInfo` — roles included — without contacting RediensIAM.
    #[test]
    fn cache_key_is_a_sha256_digest() {
        // Known-answer test: SHA-256("") — pins both the algorithm and the hex encoding.
        assert_eq!(
            cache_key(""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(cache_key("rediens_pat_supersecret").len(), 64);
    }

    /// T-N3: tenant role names are namespaced by project at the issuer, so a bare name must not
    /// authorise across tenants.
    #[test]
    fn tenant_roles_do_not_match_across_projects() {
        let info = TokenInfo {
            active: true,
            project_id: Some("project-a".into()),
            roles: vec!["project-a/admin".into()],
            ..Default::default()
        };

        assert!(info.has_project_role("project-a", "admin"));
        assert!(
            !info.has_project_role("project-b", "admin"),
            "must not serve another tenant"
        );
        assert!(
            !info.has_role("admin"),
            "a bare tenant role name must never match"
        );
        assert!(!info.has_role("super_admin"));
    }

    #[test]
    fn role_and_tenant_helpers() {
        let info = TokenInfo {
            active: true,
            org_id: Some("org-1".into()),
            roles: vec!["org_admin".into()],
            ..Default::default()
        };

        assert!(info.has_role("org_admin"));
        assert!(!info.has_role("super_admin"));
        assert!(info.belongs_to_org("org-1"));
        assert!(
            !info.belongs_to_org("org-2"),
            "must not serve another tenant"
        );
    }

    #[test]
    fn inactive_has_no_roles() {
        let info = TokenInfo::inactive();
        assert!(!info.active);
        assert!(info.roles.is_empty());
        assert!(!info.belongs_to_org("org-1"));
    }

    // ── Wire tests ────────────────────────────────────────────────────────────
    //
    // A one-shot loopback listener, so the assertions are about bytes actually sent rather than
    // about a mock the client was handed. `project_id` being absent is invisible to an old server by
    // design, so nothing short of reading the request proves it is there.

    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    /// Accepts one request, answers `body`, and yields the raw request text.
    async fn serve_once(body: &'static str) -> (String, tokio::task::JoinHandle<String>) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base_url = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());

        let handle = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();

            let mut request = Vec::new();
            loop {
                let mut chunk = [0u8; 1024];
                let n = socket.read(&mut chunk).await.unwrap();
                if n == 0 {
                    break;
                }
                request.extend_from_slice(&chunk[..n]);
                if request_is_complete(&request) {
                    break;
                }
            }

            let response = format!(
                "HTTP/1.1 200 OK\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                body.len()
            );
            socket.write_all(response.as_bytes()).await.unwrap();
            socket.flush().await.unwrap();

            String::from_utf8_lossy(&request).into_owned()
        });

        (base_url, handle)
    }

    /// Headers plus the whole declared body have arrived. Without this the test races the socket
    /// and reads only the headers on a slow machine.
    fn request_is_complete(buffer: &[u8]) -> bool {
        let text = String::from_utf8_lossy(buffer);
        let Some(head_len) = text.find("\r\n\r\n").map(|i| i + 4) else {
            return false;
        };
        let declared = text
            .lines()
            .find_map(|line| {
                line.strip_prefix("content-length: ")
                    .or(line.strip_prefix("Content-Length: "))
            })
            .and_then(|v| v.trim().parse::<usize>().ok())
            .unwrap_or(0);
        buffer.len() >= head_len + declared
    }

    /// P-06: the project id has to reach the wire. The whole change is worthless if the field is
    /// configured and then not sent.
    #[tokio::test]
    async fn introspect_sends_the_project_id() {
        let (base_url, server) = serve_once(r#"{"active":true}"#).await;
        let iam = RediensIamClient::new(config(&base_url)).unwrap();

        let info = iam.introspect("rediens_pat_x").await.unwrap();
        assert!(info.active);

        let request = server.await.unwrap();
        assert!(request.contains("POST /api/introspect"), "{request}");
        assert!(
            request.contains("project_id=proj-1"),
            "project_id missing from the form body: {request}"
        );
    }

    /// A delegated session: `act` names the operator, and the role list is empty. Both halves
    /// matter — a consumer that reads the roles and ignores `act` cannot tell support traffic from
    /// the customer's own, which is the one thing it must never fail to do.
    #[tokio::test]
    async fn introspect_surfaces_the_actor_of_a_delegated_token() {
        let (base_url, _server) = serve_once(
            r#"{"active":true,"sub":"imp_7f3","org_id":"acme","roles":[],
                "act":{"sub":"usr_operator","level":"super_admin","mode":"read","session":"7f3"}}"#,
        )
        .await;
        let iam = RediensIamClient::new(config(&base_url)).unwrap();

        let info = iam.introspect("rediens_imp_x").await.unwrap();

        let act = info.act.as_ref().expect("act must survive deserialisation");
        assert_eq!(act.sub, "usr_operator");
        assert_eq!(act.mode, "read");
        assert!(info.roles.is_empty(), "a delegated token carries no roles");
        assert!(info.is_read_only_impersonation());
    }

    /// Opening a session returns a credential shown once, and the request carries the reason —
    /// which is the field the tenant's own audit log will show them.
    #[tokio::test]
    async fn open_impersonation_posts_the_reason_and_returns_the_token() {
        let (base_url, server) = serve_once(
            r#"{"access_token":"rediens_imp_abc","session_id":"7f3","expires_in":900,
                "sub":"imp_7f3","org_id":"acme","project_id":"p1",
                "act":{"sub":"usr_operator","level":"super_admin","mode":"read","session":"7f3"}}"#,
        )
        .await;
        let iam = RediensIamClient::new(config(&base_url)).unwrap();

        let session = iam
            .open_impersonation("acme", "p1", "read", "ticket #4812", Some(900))
            .await
            .unwrap();

        assert_eq!(session.access_token, "rediens_imp_abc");
        assert_eq!(session.session_id, "7f3");
        assert_eq!(session.act.unwrap().sub, "usr_operator");

        let request = server.await.unwrap();
        assert!(
            request.contains("POST /api/manage/impersonate"),
            "{request}"
        );
        assert!(
            request.contains("ticket #4812"),
            "the reason must reach the server: {request}"
        );
    }

    /// A session with no stated reason is not auditable, so the client refuses before the round
    /// trip rather than letting the server answer 400.
    #[tokio::test]
    async fn open_impersonation_refuses_an_empty_reason() {
        let iam = RediensIamClient::new(config("http://127.0.0.1:1")).unwrap();

        let err = iam
            .open_impersonation("acme", "p1", "read", "   ", None)
            .await
            .unwrap_err();

        assert!(matches!(err, Error::Config(_)), "got {err:?}");
    }

    /// And the field is absent on everything else, which is what gives it meaning.
    #[tokio::test]
    async fn an_ordinary_token_has_no_actor() {
        let (base_url, _server) = serve_once(r#"{"active":true,"sub":"sa:1"}"#).await;
        let iam = RediensIamClient::new(config(&base_url)).unwrap();

        let info = iam.introspect("rediens_pat_x").await.unwrap();

        assert!(info.act.is_none());
        assert!(!info.is_read_only_impersonation());
    }

    /// The most common answer this endpoint gives, in the shape a server that predates the fix
    /// still sends it: every optional field explicitly null.
    ///
    /// `#[serde(default)]` fills a *missing* field and errors on an explicit null, so this used to
    /// come back as `Error::Transport`. That is the one error this crate's own documentation tells
    /// integrators to treat as "the IAM is unreachable, decide for yourself" — so a caller that
    /// degrades gracefully during an outage would have admitted every expired and revoked token.
    /// An inactive answer must deserialise as `active: false`, not as a network fault.
    #[tokio::test]
    async fn an_inactive_answer_with_null_fields_is_not_a_transport_error() {
        let (base_url, server) = serve_once(
            r#"{"active":false,"sub":null,"user_id":null,"org_id":null,"project_id":null,"roles":null,"client_id":null,"is_service_account":false}"#,
        )
        .await;
        let iam = RediensIamClient::new(config(&base_url)).unwrap();

        let info = iam
            .introspect("rediens_pat_expired")
            .await
            .expect("an inactive token is an answer, not a transport failure");

        assert!(!info.active);
        assert!(info.roles.is_empty());
        let _ = server.await.unwrap();
    }

    #[tokio::test]
    async fn authorize_sends_the_project_id() {
        let (base_url, server) = serve_once(r#"{"allowed":true}"#).await;
        let iam = RediensIamClient::new(config(&base_url)).unwrap();

        assert!(iam
            .authorize("t", "Organisations", "org-1", "org_admin")
            .await
            .unwrap());

        let request = server.await.unwrap();
        assert!(request.contains("POST /api/authorize"), "{request}");
        assert!(
            request.contains(r#""project_id":"proj-1""#),
            "project_id missing from the JSON body: {request}"
        );
    }
}
