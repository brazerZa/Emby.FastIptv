using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.FastIptv
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Fast IPTV M3U";
        public override Guid Id => new Guid("C7D8E9F0-A1B2-4C3D-8E4F-5A6B7C8D9E0F");
        public override string ConfigurationFileName => "FastIptv.xml";

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("Emby.FastIptv.Thumb.png");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "FastIptv",
                    EmbeddedResourcePath = "Emby.FastIptv.Configuration.configurationpage.html",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "FastIptvConfig",
                    EmbeddedResourcePath = "Emby.FastIptv.Configuration.configurationcontroller.js",
                    IsMainConfigPage = false
                },
                new PluginPageInfo
                {
                    Name = "FastIptvTunerSetup",
                    EmbeddedResourcePath = "Emby.FastIptv.Configuration.tunersetup.html",
                    IsMainConfigPage = false
                },
                new PluginPageInfo
                {
                    Name = "FastIptvTunerSetupCtrl",
                    EmbeddedResourcePath = "Emby.FastIptv.Configuration.tunersetupcontroller.js",
                    IsMainConfigPage = false
                },
            };
        }
    }
}
