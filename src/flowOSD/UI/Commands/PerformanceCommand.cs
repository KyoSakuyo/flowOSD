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
        private readonly IDisposable timerSubscription;
        private readonly CompositeDisposable disposables = new CompositeDisposable();

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

            // 切换时更新菜单打勾状态
            performanceService.ActiveProfile
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(profile => IsChecked = profile.Id != PerformanceProfile.DefaultId)
                .DisposeWith(disposables);

            Description = TextResources["Commands.Performance.Description"];
            Enabled = true;

            // Rx 定时器：立即触发一次，然后每 20 秒重复
            timerSubscription = Observable
                .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(20))
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(_ => Execute());

            disposables.Add(timerSubscription);
        }

        public override bool CanExecuteWithHotKey => true;

        public override async void Execute(object? parameter = null)
        {
            if (!Enabled)
                return;

            Guid nextId;
            if (parameter is Guid profileId)
            {
                nextId = profileId;
            }
            else
            {
                nextId = await GetNextProfileId();
            }

            performanceService.SetActiveProfile(nextId);
            await SaveActiveProfile(nextId);
        }

        private async Task SaveActiveProfile(Guid profileId)
        {
            if (await atk.TabletMode.FirstOrDefaultAsync() == TabletMode.Tablet)
            {
                config.Performance.TabletProfile = profileId;
            }
            else if (await powerManagement.PowerSource.FirstOrDefaultAsync() == PowerSource.Battery)
            {
                config.Performance.ChargerProfile = profileId;
            }
            else
            {
                config.Performance.BatteryProfile = profileId;
            }
        }

        private async Task<Guid> GetNextProfileId()
        {
            var profile = await performanceService.ActiveProfile.FirstAsync();
            return profile.Id switch
            {
                _ when profile.Id == PerformanceProfile.DefaultId => PerformanceProfile.TurboId,
                _ when profile.Id == PerformanceProfile.TurboId   => PerformanceProfile.SilentId,
                _                                                  => PerformanceProfile.DefaultId,
            };
        }

        public void Dispose()
        {
            disposables.Dispose();
        }
    }
}
