using System;

namespace AlUmmahNow.App.Services
{
    public sealed class AppEnvironment
    {
        public static readonly AppEnvironment Qa = new AppEnvironment(
            "qa",
            new Uri("https://videos.api.qa.alummahnow.net"),
            new Uri("https://auth.qa.alummahnow.net"),
            viewerClientId: "ce7de8a4-59dd-4682-ab4d-105399105294",
            studioClientId: "29b4792a-caad-4814-a83f-4337bc6a1aeb",
            adminClientId: "306b6f10-d9f0-4609-84a1-23c92da978e9",
            adminApi: new Uri("https://admin.api.qa.alummahnow.net"));

        public static readonly AppEnvironment Production = new AppEnvironment(
            "prod",
            new Uri("https://videos.api.alummahnow.net"),
            new Uri("https://auth.alummahnow.net"),
            viewerClientId: "f97abe68-ef61-41b6-bf08-d86650cc7cb3",
            studioClientId: "950e2b3b-6353-4472-affe-7c1f57f4e6a4",
            adminClientId: "288b80b9-ecc6-4927-8b5a-bded5aa00e55",
            adminApi: new Uri("https://admin.api.alummahnow.net"));

        private AppEnvironment(string name, Uri videosApi, Uri authIssuer, string viewerClientId, string studioClientId, string adminClientId, Uri adminApi)
        {
            Name = name;
            VideosApi = videosApi;
            AuthIssuer = authIssuer;
            ViewerClientId = viewerClientId;
            StudioClientId = studioClientId;
            AdminClientId = adminClientId;
            AdminApi = adminApi;
        }

        /// <summary>QA for development and CI; production for tagged releases.</summary>
#if AUN_PROD
        public static AppEnvironment BuildDefault => Production;
#else
        public static AppEnvironment BuildDefault => Qa;
#endif

        public string Name { get; }
        public Uri VideosApi { get; }
        public Uri AuthIssuer { get; }

        /// <summary>Public PKCE clients registered in the auth server (no secret).</summary>
        public string ViewerClientId { get; }
        public string StudioClientId { get; }

        /// <summary>"Admin (Windows)" (Auth migration 0038): redirect alummahnow-admin://auth/callback.</summary>
        public string AdminClientId { get; }

        /// <summary>admin-api, where the review queue lives.</summary>
        public Uri AdminApi { get; }

        /// <summary>Picks the environment from AUN_ENV; unset means the build default.</summary>
        public static AppEnvironment Resolve(string? value, AppEnvironment buildDefault) =>
            string.Equals(value, "prod", StringComparison.OrdinalIgnoreCase) ? Production
            : string.Equals(value, "qa", StringComparison.OrdinalIgnoreCase) ? Qa
            : buildDefault;
    }
}
