use std::fs;
use bevy::log::{error, info};
use bevy::prelude::*;

pub struct MicroSystemMetrics;
impl Plugin for MicroSystemMetrics {
    fn build(&self, app: &mut App) {
        #[cfg(target_os = "linux")]
        {
            mount_pseudo_filesystems();
            // Alpine's vmlinuz-virt ships virtio_net as a loadable module.
            // Our initramfs has no module loader, so we load the three .ko
            // files ourselves via init_module(2), in dependency order.
            for m in ["failover", "net_failover", "virtio_net"] {
                load_kernel_module(&format!("/modules/{}.ko", m));
            }
            // Then statically configure eth0 for QEMU SLIRP's 10.0.2.0/24.
            bring_up_eth0();
        }

        if cfg!(target_os = "linux") {
            app.add_systems(Update, log_metrics);
        }
    }
}

#[cfg(target_os = "linux")]
fn mount_pseudo_filesystems() {
    mount_one("proc", "/proc", "proc");
    // sysfs gives us /sys/class/net so we can detect the virtio_net interface
    // after the module loads.
    mount_one("sysfs", "/sys", "sysfs");
}

#[cfg(target_os = "linux")]
fn mount_one(source: &str, target: &str, fstype: &str) {
    use std::ffi::CString;

    if let Err(e) = std::fs::create_dir_all(target) {
        error!("Could not create {} directory: {}", target, e);
        return;
    }

    let s = CString::new(source).unwrap();
    let t = CString::new(target).unwrap();
    let f = CString::new(fstype).unwrap();
    let rc = unsafe { libc::mount(s.as_ptr(), t.as_ptr(), f.as_ptr(), 0, std::ptr::null()) };
    if rc == 0 {
        info!("mounted {} at {}", fstype, target);
    } else {
        error!(
            "mount {} at {} failed: {}",
            fstype,
            target,
            std::io::Error::last_os_error()
        );
    }
}

// init_module(2): load a .ko image into the running kernel without modprobe.
#[cfg(target_os = "linux")]
fn load_kernel_module(path: &str) {
    let bytes = match std::fs::read(path) {
        Ok(b) => b,
        Err(e) => {
            error!("[modload] read {}: {}", path, e);
            return;
        }
    };
    let params = b"\0";
    let rc = unsafe {
        libc::syscall(
            libc::SYS_init_module,
            bytes.as_ptr(),
            bytes.len() as libc::c_ulong,
            params.as_ptr(),
        )
    };
    if rc < 0 {
        error!(
            "[modload] init_module {}: {}",
            path,
            std::io::Error::last_os_error()
        );
    } else {
        info!("[modload] loaded {} ({} bytes)", path, bytes.len());
    }
}

// Bring up eth0 with a static IPv4 address — kernel ABI, no userspace tools
// needed. SLIRP's gateway 10.0.2.2 is on the same /24, so we don't need a
// default route to reach the host.
#[cfg(target_os = "linux")]
fn bring_up_eth0() {
    use std::io::Error as IoErr;

    const IFNAMSIZ: usize = 16;
    // `libc::Ioctl` is a target-specific alias (i32 on musl, u64 on glibc, etc.).
    const SIOCSIFADDR: libc::Ioctl = 0x8916 as libc::Ioctl;
    const SIOCSIFNETMASK: libc::Ioctl = 0x891c as libc::Ioctl;
    const SIOCSIFFLAGS: libc::Ioctl = 0x8914 as libc::Ioctl;
    const IFF_UP: i16 = 0x1;
    const IFF_RUNNING: i16 = 0x40;

    #[repr(C)]
    struct IfReq {
        ifr_name: [u8; IFNAMSIZ],
        ifr_payload: [u8; 24], // union sized for sockaddr / ifmap / etc.
    }

    fn make_ifreq(name: &str) -> IfReq {
        let mut req = IfReq {
            ifr_name: [0; IFNAMSIZ],
            ifr_payload: [0; 24],
        };
        let bytes = name.as_bytes();
        let n = bytes.len().min(IFNAMSIZ - 1);
        req.ifr_name[..n].copy_from_slice(&bytes[..n]);
        req
    }

    // sockaddr_in: u16 family, u16 port, u32 s_addr (raw network-order bytes), u8 zero[8]
    fn write_inaddr(req: &mut IfReq, ipv4: [u8; 4]) {
        req.ifr_payload.fill(0);
        let fam = (libc::AF_INET as u16).to_ne_bytes();
        req.ifr_payload[0..2].copy_from_slice(&fam);
        req.ifr_payload[4..8].copy_from_slice(&ipv4);
    }

    fn write_flags(req: &mut IfReq, flags: i16) {
        req.ifr_payload.fill(0);
        req.ifr_payload[0..2].copy_from_slice(&flags.to_ne_bytes());
    }

    // Wait for the kernel to probe virtio-net and register the interface.
    // /sys/class/net lists every netdev including `lo`. Total wait: 5 s.
    let mut iface: Option<String> = None;
    let mut last_listing = String::new();
    for _ in 0..250 {
        if let Ok(entries) = std::fs::read_dir("/sys/class/net") {
            let mut names: Vec<String> = entries
                .flatten()
                .map(|e| e.file_name().to_string_lossy().into_owned())
                .collect();
            names.sort();
            last_listing = names.join(",");
            if let Some(n) = names.iter().find(|n| *n != "lo") {
                iface = Some(n.clone());
                break;
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    let Some(iface) = iface else {
        error!(
            "[eth0] no non-loopback netdev after 5s. /sys/class/net = [{}]",
            last_listing
        );
        return;
    };
    info!("[eth0] using interface '{}'", iface);

    let sock = unsafe { libc::socket(libc::AF_INET, libc::SOCK_DGRAM, 0) };
    if sock < 0 {
        error!("[eth0] socket: {}", IoErr::last_os_error());
        return;
    }

    let ipv4: [u8; 4] = [10, 0, 2, 15];
    let mask: [u8; 4] = [255, 255, 255, 0];

    let mut req = make_ifreq(&iface);
    write_inaddr(&mut req, ipv4);
    if unsafe { libc::ioctl(sock, SIOCSIFADDR, &req as *const _) } < 0 {
        error!("[eth0] SIOCSIFADDR: {}", IoErr::last_os_error());
        unsafe { libc::close(sock) };
        return;
    }

    let mut req = make_ifreq(&iface);
    write_inaddr(&mut req, mask);
    if unsafe { libc::ioctl(sock, SIOCSIFNETMASK, &req as *const _) } < 0 {
        error!("[eth0] SIOCSIFNETMASK: {}", IoErr::last_os_error());
        unsafe { libc::close(sock) };
        return;
    }

    let mut req = make_ifreq(&iface);
    write_flags(&mut req, IFF_UP | IFF_RUNNING);
    if unsafe { libc::ioctl(sock, SIOCSIFFLAGS, &req as *const _) } < 0 {
        error!("[eth0] SIOCSIFFLAGS: {}", IoErr::last_os_error());
        unsafe { libc::close(sock) };
        return;
    }

    unsafe { libc::close(sock) };
    info!(
        "[eth0] up {}.{}.{}.{}/{}.{}.{}.{}",
        ipv4[0], ipv4[1], ipv4[2], ipv4[3], mask[0], mask[1], mask[2], mask[3]
    );
}

fn read_memory_usage() -> Option<f64> {
    let status = fs::read_to_string("/proc/self/statm").ok()?;
    let parts: Vec<&str> = status.split_whitespace().collect();

    if let Some(rss_pages) = parts.get(1) {
        let pages: f64 = rss_pages.parse().unwrap_or(0.0);
        let megabytes = (pages * 4096.0) / (1024.0 * 1024.0);
        return Some(megabytes);
    }
    None
}

fn read_total_vm_memory() -> Option<f64> {
    // Returns MemTotal - MemAvailable in MB — i.e. "memory in use by the VM."
    // No hardcoded ceiling so the figure stays accurate if QEMU's -m changes.
    let meminfo = fs::read_to_string("/proc/meminfo").ok()?;
    let mut total_kb: Option<f64> = None;
    let mut avail_kb: Option<f64> = None;
    for line in meminfo.lines() {
        let parts: Vec<&str> = line.split_whitespace().collect();
        let kb = parts.get(1).and_then(|s| s.parse::<f64>().ok());
        if line.starts_with("MemTotal:") {
            total_kb = kb;
        } else if line.starts_with("MemAvailable:") {
            avail_kb = kb;
        }
    }
    let (t, a) = (total_kb?, avail_kb?);
    Some((t - a) / 1024.0)
}

fn log_metrics(time: Res<Time>, mut timer: Local<f32>) {
    *timer += time.delta().as_secs_f32();
    if *timer > 5.0 {
        // 3. Add explicit error handling so it doesn't fail silently
        match (read_memory_usage(), read_total_vm_memory()) {
            (Some(mb), Some(mb_vm)) => {
                info!("SERVER STATS | RAM (RSS): {:.2} MB | RAM (VM) : {:.2} MB | Uptime: {:.1}s",
                      mb, mb_vm, time.elapsed_secs());
            }
            _ => {
                error!("Could not read memory metrics from /proc! Is the pseudo-filesystem mounted?");
            }
        }
        *timer = 0.0;
    }
}