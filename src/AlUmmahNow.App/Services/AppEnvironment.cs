using System;

namespace AlUmmahNow.App.Services
{
    public sealed class AppEnvironment
    {
        public static readonly AppEnvironment Qa = new AppEnvironment("qa", new Uri("https://videos.api.qa.alummahnow.net"));
        public static readonly AppEnvironment Production = new AppEnvironment("prod", new Uri("https://videos.api.alummahnow.net"));

        private AppEnvironment(string name, Uri videosApi)
        {
            Name = name;
            VideosApi = videosApi;
        }

        public string Name { get; }
        public Uri VideosApi { get; }

        /// <summary>Picks the environment from AUN_ENV; unset means the build default.</summary>
        public static AppEnvironment Resolve(string? value, AppEnvironment buildDefault) =>
            string.Equals(value, "prod", StringComparison.OrdinalIgnoreCase) ? Production
            : string.Equals(value, "qa", StringComparison.OrdinalIgnoreCase) ? Qa
            : buildDefault;
    }
}
