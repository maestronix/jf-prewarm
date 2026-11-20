using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// Episode Prewarm plugin entry point.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginGuid = Guid.Parse("d1c4b6e8-8e2a-4b63-9f13-7a2e5d9c41f0");

    public static Plugin Instance { get; private set; } = null!;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Episode Prewarm";

    public override Guid Id => PluginGuid;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "jfprewarm.html",
                EmbeddedResourcePath = "Jellyfin.Plugin.JfPrewarm.Configuration.configPage.html"
            }
        ];
    }
}
