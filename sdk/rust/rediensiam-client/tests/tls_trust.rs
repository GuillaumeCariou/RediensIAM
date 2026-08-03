//! R-32: the client must validate against the host's trust store, not only against the CA bundle
//! compiled into the binary.
//!
//! Both cases run a real TLS handshake against a real rustls server holding a real certificate.
//! Nothing weaker proves either half: a mock proves the code was called, not that rustls accepted
//! the chain, and "does it compile with the feature on" proves nothing about what is trusted.
//!
//! `SSL_CERT_FILE` is how the host trust store is named on this platform — `rustls-native-certs`
//! reads it through `openssl-probe`, exactly as OpenSSL, curl and the .NET runtime do. Pointing it
//! at a CA is the same act as dropping that CA into `/etc/ssl/certs`, without a test needing root.
//! It is *not* a test of the macOS Keychain or the Windows certificate store; those share the
//! `rustls-native-certs` entry point but not this code path.

use std::net::{Ipv4Addr, SocketAddr};
use std::sync::Arc;

use rcgen::{BasicConstraints, CertificateParams, DnType, IsCa, Issuer, KeyPair, KeyUsagePurpose};
use rediensiam_client::{Config, Error, RediensIamClient};
use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

/// A CA and one leaf it signed for `127.0.0.1`.
struct Pki {
    ca_pem: String,
    leaf_chain: Vec<CertificateDer<'static>>,
    leaf_key: PrivateKeyDer<'static>,
}

fn issue(common_name: &str) -> Pki {
    let ca_key = KeyPair::generate().unwrap();
    let mut ca_params = CertificateParams::new(Vec::<String>::new()).unwrap();
    ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    ca_params.key_usages = vec![KeyUsagePurpose::KeyCertSign, KeyUsagePurpose::CrlSign];
    ca_params.distinguished_name.push(DnType::CommonName, common_name);
    let ca_cert = ca_params.self_signed(&ca_key).unwrap();

    let leaf_key = KeyPair::generate().unwrap();
    let leaf_params = CertificateParams::new(vec!["127.0.0.1".to_string()]).unwrap();
    let issuer = Issuer::from_params(&ca_params, &ca_key);
    let leaf_cert = leaf_params.signed_by(&leaf_key, &issuer).unwrap();

    Pki {
        ca_pem: ca_cert.pem(),
        leaf_chain: vec![leaf_cert.der().clone(), ca_cert.der().clone()],
        leaf_key: PrivateKeyDer::Pkcs8(leaf_key.serialize_der().into()),
    }
}

/// Serves one TLS connection and answers a valid introspection response. Returns once bound, so
/// the client cannot race the listener.
async fn serve_once_tls(pki: &Pki) -> SocketAddr {
    let config = rustls::ServerConfig::builder_with_provider(Arc::new(
        rustls::crypto::ring::default_provider(),
    ))
    .with_safe_default_protocol_versions()
    .unwrap()
    .with_no_client_auth()
    .with_single_cert(pki.leaf_chain.clone(), pki.leaf_key.clone_key())
    .unwrap();

    let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(config));
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await.unwrap();
    let addr = listener.local_addr().unwrap();

    tokio::spawn(async move {
        let (tcp, _) = listener.accept().await.unwrap();
        // The untrusted case aborts here, in the handshake. That is the point of it, so a failure
        // must not panic the runtime.
        let Ok(mut tls) = acceptor.accept(tcp).await else {
            return;
        };

        let mut buffer = [0u8; 2048];
        let _ = tls.read(&mut buffer).await;

        const BODY: &str = r#"{"active":true,"ver":1}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{BODY}",
            BODY.len()
        );
        let _ = tls.write_all(response.as_bytes()).await;
        let _ = tls.shutdown().await;
    });

    addr
}

fn client(addr: SocketAddr) -> Result<RediensIamClient, Error> {
    RediensIamClient::new(Config {
        base_url: format!("https://{addr}"),
        service_account_token: "rediens_pat_x".into(),
        audience: "proj-1".into(),
        ..Default::default()
    })
}

/// Every error in the chain, flattened — rustls' verdict is three `source()` hops below `Error`.
fn chain(err: &Error) -> String {
    let mut out = err.to_string();
    let mut source = std::error::Error::source(err);
    while let Some(inner) = source {
        out.push_str(" <- ");
        out.push_str(&inner.to_string());
        source = inner.source();
    }
    out
}

/// One test, not two: `SSL_CERT_FILE` is process-wide and reqwest reads it when the client is
/// built, so two `#[tokio::test]`s would race each other's trust store.
#[tokio::test]
async fn private_ca_is_trusted_and_an_unknown_one_is_not() {
    let dir = std::env::temp_dir().join(format!("rediensiam-tls-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();

    let deployment_ca = issue("RediensIAM deployment CA");
    let stranger = issue("Some other CA");

    // ── Trusted: the operator's CA is in the host store ──────────────────────
    let trusted_path = dir.join("deployment-ca.pem");
    std::fs::write(&trusted_path, &deployment_ca.ca_pem).unwrap();
    std::env::set_var("SSL_CERT_FILE", &trusted_path);

    let addr = serve_once_tls(&deployment_ca).await;
    let info = client(addr)
        .expect("construction must succeed")
        .introspect("rediens_pat_x")
        .await
        .unwrap_or_else(|e| panic!("a host-trusted private CA must validate: {}", chain(&e)));
    assert!(info.active);

    // ── Untrusted: same server, a host store that does not hold its CA ───────
    // The stranger CA is present and valid, so this is a trust decision, not an empty store.
    let untrusted_path = dir.join("stranger-ca.pem");
    std::fs::write(&untrusted_path, &stranger.ca_pem).unwrap();
    std::env::set_var("SSL_CERT_FILE", &untrusted_path);

    let addr = serve_once_tls(&deployment_ca).await;
    let err = client(addr)
        .expect("construction must succeed")
        .introspect("rediens_pat_x")
        .await
        .expect_err("a certificate from an untrusted CA must still be refused");

    let text = chain(&err);
    assert!(matches!(err, Error::Transport(_)), "{text}");
    assert!(
        text.contains("certificate") || text.contains("UnknownIssuer"),
        "the refusal must come from certificate validation, not from anything else: {text}"
    );

    std::env::remove_var("SSL_CERT_FILE");
    let _ = std::fs::remove_dir_all(&dir);
}
