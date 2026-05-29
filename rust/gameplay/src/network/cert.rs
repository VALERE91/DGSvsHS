use std::path::PathBuf;

// Regenerate with:
//   openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
//     -nodes -days 3650 -keyout key.pem -out cert.pem -subj "/CN=dgsvshs"
const CERT_PEM: &[u8] = include_bytes!("certs/cert.pem");
const KEY_PEM: &[u8] = include_bytes!("certs/key.pem");

pub struct CertFiles {
    pub cert: PathBuf,
    pub key: PathBuf,
}

// quiche only loads cert/key from disk paths, so the embedded PEMs are
// materialised to tmpfs at startup. In the microVM initramfs `/tmp` doesn't
// exist out of the box — create it before writing.
pub fn write_to_tmp() -> std::io::Result<CertFiles> {
    let dir = std::env::temp_dir();
    std::fs::create_dir_all(&dir)?;
    let cert = dir.join("dgsvshs_cert.pem");
    let key = dir.join("dgsvshs_key.pem");
    std::fs::write(&cert, CERT_PEM)?;
    std::fs::write(&key, KEY_PEM)?;
    Ok(CertFiles { cert, key })
}
