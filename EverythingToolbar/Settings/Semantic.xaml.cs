using System;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using EverythingToolbar.App.Semantic;

namespace EverythingToolbar.Settings
{
    [ObservableObject]
    public partial class Semantic
    {
        public ISettings Settings { get; } = Ioc.Default.GetRequiredService<ISettings>();

        public SemanticState State { get; } = Ioc.Default.GetRequiredService<SemanticState>();

        private readonly SemanticIndexBuilder _builder = Ioc.Default.GetRequiredService<SemanticIndexBuilder>();

        public Semantic()
        {
            InitializeComponent();
            DataContext = this;

            State.PropertyChanged += OnStateChanged;
            Settings.PropertyChanged += OnSettingsChanged;
        }

        // The page is recreated on every navigation; without this the singleton services would
        // accumulate a handler per visit.
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            State.PropertyChanged -= OnStateChanged;
            Settings.PropertyChanged -= OnSettingsChanged;
        }

        public string StatusText
        {
            get
            {
                return State.Status switch
                {
                    SemanticSearchStatus.Disabled => Properties.Resources.SettingsSemanticStatusDisabled,
                    SemanticSearchStatus.Indexing => string.Format(
                        Properties.Resources.SettingsSemanticIndexedOfTotal,
                        State.IndexedCount,
                        Math.Max(State.TotalCount, State.IndexedCount)
                    ),
                    SemanticSearchStatus.Paused => string.Format(
                        Properties.Resources.SettingsSemanticIndexedOfTotal,
                        State.IndexedCount,
                        Math.Max(State.TotalCount, State.IndexedCount)
                    ),
                    SemanticSearchStatus.Unavailable => Properties.Resources.SettingsSemanticStatusUnavailable,
                    SemanticSearchStatus.Ready => string.Format(Properties.Resources.SettingsSemanticImagesIndexed, State.IndexedCount)
                        + (
                            State.LastBuildTimeUtc.HasValue
                                ? " · "
                                    + string.Format(
                                        Properties.Resources.SettingsSemanticLastBuild,
                                        State.LastBuildTimeUtc.Value.ToLocalTime().ToString("g")
                                    )
                                : ""
                        ),
                    _ => "",
                };
            }
        }

        public string StatusHelpText =>
            State.Status == SemanticSearchStatus.Unavailable && !string.IsNullOrEmpty(State.StatusMessage)
                ? State.StatusMessage
                : Properties.Resources.SettingsSemanticStatusHelp;

        public bool CanBuild => !State.IsIndexing && Settings.IsSemanticSearchEnabled;

        private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusHelpText));
            OnPropertyChanged(nameof(CanBuild));
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISettings.IsSemanticSearchEnabled))
                OnPropertyChanged(nameof(CanBuild));
        }

        private void OnBuildClicked(object sender, RoutedEventArgs e)
        {
            if (!Settings.IsSemanticSearchEnabled)
                Settings.IsSemanticSearchEnabled = true;

            _builder.StartIncremental();
        }

        private void OnPauseClicked(object sender, RoutedEventArgs e)
        {
            _builder.Pause();
        }

        private void OnRebuildClicked(object sender, RoutedEventArgs e)
        {
            if (!Settings.IsSemanticSearchEnabled)
                Settings.IsSemanticSearchEnabled = true;

            _builder.BeginBuild(rebuild: true);
        }
    }
}
