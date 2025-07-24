using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using flowOSD.Core.Configs;
using flowOSD.Core.Hardware;
using flowOSD.Core.Resources;
using flowOSD.Extensions;

namespace flowOSD.UI.Commands
{
    public class PerformanceCommand : CommandBase, IDisposable
    {
        private readonly IConfig config;
        private readonly IAtk atk;
        private readonly IPowerManagement powerManagement;
        private readonly IPerformanceService performanceService;
        private readonly CompositeDisposable disposables = new CompositeDisposable();
        private readonly System.Timers.Timer timer;

        public PerformanceCommand(
            ITextResources textResources,
            IImageResources imageResources,
            IConfig config,
            IAtk atk,
            IPowerManagement powerManagement,
            IPerformanceService performanceService)
            : base(textResources, imageResources)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.atk = atk ?? throw new ArgumentNullException(nameof(atk));
            this.powerManagement = powerManagement ?? throw new ArgumentNullException(nameof(powerManagement));
            this.performanceService = performanceService ?? throw new ArgumentNullException(nameof(performanceService));

            performanceService.ActiveProfile
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(profile => IsChecked = profile.Id != PerformanceProfile.DefaultId)
                .DisposeWith(disposables);

            Description = TextResources["Commands.Performance.Description"];
            Enabled = true;

            // 这里全限定到 System.Timers.Timer，避免跟 System.Threading.Timer 冲突
            timer = new System.Timers.Timer(20_000) {
                AutoReset = true,
                Enabled   = true
            };
            timer.Elapsed += OnTimerElapsed;
        }

        private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            SynchronizationContext.Current?.Post(_ =>
                Execute(PerformanceProfile.TurboId), null);
        }

        public override bool CanExecuteWithHotKey => true;

        public override async void Execute(object? parameter = null)
        {
            if (!Enabled) return;

            if (parameter is Guid profileId)
            {
                performanceService.SetActiveProfile(profileId);
                await SaveActiveProfile(profileId);
            }
        }

        private async Task SaveActiveProfile(Guid profileId)
        {
            if (await atk.TabletMode.FirstOrDefaultAsync() == TabletMode.Tablet)
                config.Performance.TabletProfile = profileId;
            else if (await powerManagement.PowerSource.FirstOrDefaultAsync() == PowerSource.Battery)
                config.Performance.ChargerProfile = profileId;
            else
                config.Performance.BatteryProfile = profileId;
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Elapsed -= OnTimerElapsed;
            timer.Dispose();
            disposables.Dispose();
        }
    }
}
