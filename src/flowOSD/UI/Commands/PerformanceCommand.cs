using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Timers;
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
        private readonly Timer timer;

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

            // 订阅 Profile 变更，用于菜单中打勾状态同步
            performanceService.ActiveProfile
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(profile => IsChecked = profile.Id != PerformanceProfile.DefaultId)
                .DisposeWith(disposables);

            Description = TextResources["Commands.Performance.Description"];
            Enabled = true;

            // 每 20 秒触发一次，将 Turbo 档 ID 传入 Execute
            timer = new Timer(20_000) {
                AutoReset = true,
                Enabled   = true
            };
            timer.Elapsed += OnTimerElapsed;
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 确保回到 UI 线程再调用 Execute(Guid)
            SynchronizationContext.Current?.Post(_ =>
                Execute(PerformanceProfile.TurboId), null);
        }

        public override bool CanExecuteWithHotKey => true;

        /// <summary>
        /// 切换到指定档位；当 parameter 是 Guid 时，直接使用它。
        /// </summary>
        public override async void Execute(object? parameter = null)
        {
            if (!Enabled) return;

            if (parameter is Guid profileId)
            {
                performanceService.SetActiveProfile(profileId);
                await SaveActiveProfile(profileId);
            }
        }

        /// <summary>
        /// 根据当前模式（平板/插电/电池）保存对应的 Profile ID。
        /// </summary>
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

        public void Dispose()
        {
            // 停止并释放定时器
            timer.Stop();
            timer.Elapsed -= OnTimerElapsed;
            timer.Dispose();

            // 清理 Rx 订阅
            disposables.Dispose();
        }
    }
}
