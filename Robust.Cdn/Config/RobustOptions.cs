namespace Robust.Cdn.Config;

public sealed class RobustOptions
{
    public const string Position = "Robust";
    public string FileDiskPath { get; set; } = "";
    public string PublishToken { get; set; } = "";
    public bool AllowRepublish { get; set; } = false;
    public string ClientZipName { get; set; } = "Robust.Client";
    public string ServerZipName { get; set; } = "Robust.Server_";
    public bool IncludeClientPlatformsInManifest { get; set; } = true;
    public bool Private { get; set; } = false;
    public Dictionary<string, string> PrivateUsers { get; set; } = new();
}

