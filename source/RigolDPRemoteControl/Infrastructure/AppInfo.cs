using System.Reflection;

namespace RigolDPRemoteControl.Infrastructure;

/// <summary>Application name, version and links (shown in the About screen).</summary>
public static class AppInfo
{
    public const string Name = "Rigol DP Remote Control";
    public const string Author = "Wim Huyghe";

    /// <summary>GitHub repository with the source code, the help pages and the downloads ("" = not published).</summary>
    public const string RepositoryUrl = "https://github.com/WimHuyghe/RigolDPRemoteControl";

    /// <summary>"v1.00" – major.minor of the assembly version, minor with two digits.</summary>
    public static string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
            return $"v{v.Major}.{v.Minor:00}";
        }
    }
}
