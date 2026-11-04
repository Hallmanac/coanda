namespace Coanda.Daemon.Api.Endpoints;

public sealed record DaemonStatusView(int Pid, int Port, DateTimeOffset StartedAt, string Version, string Home, string Database);
