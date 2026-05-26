fn main() {
    tracing_subscriber::fmt::init();
    gameplay::setup_server();
    gameplay::launch_server();
}
