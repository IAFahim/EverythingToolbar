using System.IO;
using CommunityToolkit.Mvvm.DependencyInjection;
using Config.Net;
using EverythingToolbar.App.Semantic;
using EverythingToolbar.Core.Platform;
using EverythingToolbar.Search;
using EverythingToolbar.Semantic;
using EverythingToolbar.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EverythingToolbar
{
    public static class AppServices
    {
        private static bool IsInitialized;

        public static void Initialize()
        {
            if (IsInitialized)
                return;
            IsInitialized = true;

            var services = new ServiceCollection();
            services.AddSettings().AddPlatformAdapters().AddSearchEngine().AddSemanticSearch().AddShellServices().AddViewModels();

            var provider = services.BuildServiceProvider();
            Ioc.Default.ConfigureServices(provider);

            var settings = provider.GetRequiredService<ISettings>();

            var router = provider.GetRequiredService<EverythingClientRouter>();
            router.SetInstanceName(settings.InstanceName);
            router.SetForceLegacySdk(settings.IsForceLegacySdk);

            if (settings.IsSemanticSearchEnabled)
                provider.GetRequiredService<SemanticIndexBuilder>().StartIncremental();
        }
    }

    internal static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSettings(this IServiceCollection services)
        {
            return services.AddSingleton<ISettings>(_ =>
            {
                var store = new ConfigurationBuilder<IToolbarSettings>()
                    .UseIniFile(Path.Combine(ConfigPaths.GetConfigDirectory(), "settings.ini"))
                    .Build();
                return SettingsProxy.Create(store);
            });
        }

        public static IServiceCollection AddPlatformAdapters(this IServiceCollection services)
        {
            return services
                .AddSingleton<EverythingIpcClient>()
                .AddSingleton<EverythingPipeClient>()
                .AddSingleton<EverythingClientRouter>()
                .AddSingleton<IEverythingClient>(sp => sp.GetRequiredService<EverythingClientRouter>())
                .AddSingleton<IClipboard, ClipboardAdapter>()
                .AddSingleton<IShellDialogs, ShellDialogsAdapter>()
                .AddSingleton<INotifier, NotifierAdapter>()
                .AddSingleton<IFileLauncher, FileLauncherAdapter>()
                .AddSingleton<IFilePreviewer, FilePreviewerAdapter>()
                .AddSingleton<IAutostart, AutostartAdapter>();
        }

        // Search domain: query state, sessions, filters, commands, and result actions.
        public static IServiceCollection AddSearchEngine(this IServiceCollection services)
        {
            return services
                .AddSingleton<SearchHistory>()
                .AddSingleton<IFilterNames, FilterNames>()
                .AddSingleton<DefaultFilterProvider>()
                .AddSingleton<EverythingFilterProvider>()
                .AddSingleton<FilterProvider>()
                .AddSingleton<SearchState>()
                .AddSingleton<SearchSession>()
                .AddSingleton<SearchCommands>()
                .AddSingleton<CustomActionService>()
                .AddSingleton<SearchResultActions>()
                .AddSingleton<EverythingSearchLauncher>();
        }

        // Semantic search: EmbeddingGemma vectors served by a local Ollama instance.
        public static IServiceCollection AddSemanticSearch(this IServiceCollection services)
        {
            return services
                .AddSingleton<IEmbeddingClient, OllamaEmbeddingClient>()
                .AddSingleton<IEmbeddingImageEncoder, WicImageEncoder>()
                .AddSingleton<SemanticIndex>()
                .AddSingleton<SemanticState>()
                .AddSingleton<SemanticIndexBuilder>()
                .AddSingleton<SemanticSearchService>();
        }

        // Windowing, theming, OS policy, and the global input hooks that drive the search window.
        public static IServiceCollection AddShellServices(this IServiceCollection services)
        {
            return services
                .AddSingleton<WindowsPolicy>()
                .AddSingleton<ThemeService>()
                .AddSingleton<StartMenuSearchInterceptor>()
                .AddSingleton<GlobalShortcutListener>()
                .AddSingleton<SearchWindow>()
                .AddSingleton<SearchWindowController>()
                .AddSingleton<ISearchWindowController>(sp => sp.GetRequiredService<SearchWindowController>())
                .AddSingleton<SearchHost>();
        }

        public static IServiceCollection AddViewModels(this IServiceCollection services)
        {
            return services
                .AddTransient<SearchResultsViewModel>()
                .AddTransient<SearchResultPreviewPaneViewModel>()
                .AddTransient<SearchBoxViewModel>()
                .AddTransient<FilterSelectorViewModel>()
                .AddTransient<SettingsControlViewModel>()
                .AddTransient<SearchWindowViewModel>()
                .AddTransient<SearchButtonViewModel>()
                .AddTransient<ToolbarControlViewModel>();
        }
    }
}
