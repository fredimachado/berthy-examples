internal sealed record BuildInfo(string Version, string Commit, string? Replica)
{
    internal static BuildInfo ReadBuildInfo(IConfiguration configuration, string? hostName)
    {
        var version = configuration["BuildVersion"];
        if (string.IsNullOrEmpty(version))
        {
            version = "dev";
        }

        var commit = configuration["BuildCommit"];
        if (string.IsNullOrEmpty(commit))
        {
            commit = "unknown";
        }

        var replica = string.IsNullOrEmpty(hostName) ? null : hostName;
        return new BuildInfo(version, commit, replica);
    }
}
