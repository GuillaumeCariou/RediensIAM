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
    #[serde(default)]
    pub roles: Vec<String>,
    #[serde(default)]
    pub client_id: Option<String>,
    #[serde(default)]
    pub is_service_account: bool,
}

impl TokenInfo {
    /// An inactive answer. Returned for empty tokens without a round-trip.
    pub fn inactive() -> Self {
        Self::default()
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
        self.roles.iter().any(|r| *r == qualified)
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
    pub base_url: String,

    /// Credential this service presents. A service-account personal access token
    /// (`rediens_pat_…`) is the simplest option.
    pub service_account_token: String,

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

        let url = format!("{}/api/introspect", self.config.base_url.trim_end_matches('/'));
        let response = self
            .http
            .post(url)
            .bearer_auth(&self.config.service_account_token)
            .form(&[("token", token), ("token_type_hint", "access_token")])
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
        let url = format!("{}/api/authorize", self.config.base_url.trim_end_matches('/'));
        let response = self
            .http
            .post(url)
            .bearer_auth(&self.config.service_account_token)
            .json(&AuthorizeRequest { token, namespace, object, relation })
            .send()
            .await?;

        let parsed: AuthorizeResponse = self.parse(response).await?;
        Ok(parsed.allowed)
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
            return Err(Error::Api { status: status.as_u16(), body });
        }
        Ok(response.json().await?)
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requires_base_url_and_token() {
        assert!(RediensIamClient::new(Config::default()).is_err());

        assert!(RediensIamClient::new(Config {
            base_url: "https://auth.example.com".into(),
            ..Default::default()
        })
        .is_err());
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
        assert!(!info.has_project_role("project-b", "admin"), "must not serve another tenant");
        assert!(!info.has_role("admin"), "a bare tenant role name must never match");
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
        assert!(!info.belongs_to_org("org-2"), "must not serve another tenant");
    }

    #[test]
    fn inactive_has_no_roles() {
        let info = TokenInfo::inactive();
        assert!(!info.active);
        assert!(info.roles.is_empty());
        assert!(!info.belongs_to_org("org-1"));
    }
}
