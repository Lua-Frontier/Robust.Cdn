namespace Robust.Cdn.Config;

public sealed class LauncherOptions
{
    public const string Position = "Launcher";
    public string FileDiskPath { get; set; } = "";
    public string PublishToken { get; set; } = "";
    public Dictionary<string, string> PrivateUsers { get; set; } = new();
    public string CdnUrl { get; set; } = "";
}

