using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;       // 注意这里
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
        private readonly DispatcherTimer dispatcherTimer;

        public PerformanceCommand(
            ITextResources textResources,
            IImageResources imageResources,
            IConfig config,
            IAtk atk,
            IPowerManagement powerManagement,
            IPerformanceService performanceService)
            : base(textResources, imageResources)
        {
            this.config             = config ?? throw new ArgumentNullException(nameof(config));
            this.atk                = atk ?? throw new ArgumentNullException(nameof(atk));
            this.powerManagement    = powerManagement ?? throw new ArgumentNullException(nameof(powerManagement));
            this.performanceService = performanceService ?? throw new ArgumentNullException(nameof(performanceService));

            // 同步菜单勾选状态
            performanceService.ActiveProfile
                .ObserveOn(Dispatcher.CurrentDispatcher)
                .Subscribe(profile => IsChecked = profile.Id != PerformanceProfile.DefaultId)
                .DisposeWith(disposables);

            Description = TextResources["Commands.Performance.Description"];
            Enabled     = true;

            // UI 线程上的定时器：立刻启动，20s 间隔
            dispatcherTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(20),
                DispatcherPriority.Normal,
                (s, e) => Execute(PerformanceProfile.TurboId),
                Dispatcher.CurrentDispatcher);

            dispatcherTimer.Start();
        }

        public override bool CanExecuteWithHotKey => true;

        /// <summary>
        /// 专门处理 Guid 参数：每次都会收到 TurboId，直接切档并保存
        /// </summary>
        public override async void Execute(object? parameter = null)
        {
            if (!Enabled || parameter is not Guid profileId)
                return;

            performanceService.SetActiveProfile(profileId);
            await SaveActiveProfile(profileId);
        }

        private async Task SaveActiveProfile(Guid profileId)
        {
            // 根据当前模式（平板/插电/电池）写入不同配置项
            if (await atk.TabletMode.FirstOrDefaultAsync() == TabletMode.Tablet)
                config.Performance.TabletProfile = profileId;
            else if (await powerManagement.PowerSource.FirstOrDefaultAsync() == PowerSource.Battery)
                config.Performance.ChargerProfile = profileId;
            else
                config.Performance.BatteryProfile = profileId;
        }

        public void Dispose()
        {
            dispatcherTimer.Stop();
            disposables.Dispose();
        }
    }
}
