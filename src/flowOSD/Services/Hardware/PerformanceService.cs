using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using flowOSD.Core;
using flowOSD.Core.Configs;
using flowOSD.Core.Hardware;
using flowOSD.Core.Resources;
using flowOSD.Extensions;

namespace flowOSD.Services.Hardware
{
    sealed class PerformanceService : IDisposable, IPerformanceService
    {
        private CompositeDisposable? disposable = new CompositeDisposable();

        private readonly ITextResources textResources;
        private readonly IConfig config;
        private readonly INotificationService notificationService;
        private readonly IAtk atk;
        private readonly IPowerManagement powerManagement;

        private readonly BehaviorSubject<PerformanceProfile> activeProfileSubject;

        // 用于 Turbo 循环的 CancellationTokenSource
        private CancellationTokenSource? turboCts;

        public PerformanceService(
            ITextResources textResources,
            IConfig config,
            INotificationService notificationService,
            IAtk atk,
            IPowerManagement powerManagement)
        {
            this.textResources = textResources ?? throw new ArgumentNullException(nameof(textResources));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.atk = atk ?? throw new ArgumentNullException(nameof(atk));
            this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            this.powerManagement = powerManagement ?? throw new ArgumentNullException(nameof(powerManagement));

            DefaultProfile = new PerformanceProfile(
                PerformanceProfile.DefaultId,
                textResources["PerformanceMode.Performance"],
                PerformanceMode.Performance);

            TurboProfile = new PerformanceProfile(
                PerformanceProfile.TurboId,
                textResources["PerformanceMode.Turbo"],
                PerformanceMode.Turbo);

            SilentProfile = new PerformanceProfile(
                PerformanceProfile.SilentId,
                textResources["PerformanceMode.Silent"],
                PerformanceMode.Silent);

            activeProfileSubject = new BehaviorSubject<PerformanceProfile>(DefaultProfile);
            activeProfileSubject
                .Skip(1)
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(ApplyProfile)
                .DisposeWith(disposable);

            ActiveProfile = activeProfileSubject.AsObservable();

            // 监听电源来源和平板模式变化
            powerManagement.PowerSource
                .Throttle(TimeSpan.FromSeconds(2))
                .CombineLatest(
                    atk.TabletMode.Throttle(TimeSpan.FromSeconds(2)),
                    (powerSource, tabletMode) => (powerSource, tabletMode))
                .Throttle(TimeSpan.FromMilliseconds(2))
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(x => ChangeActiveProfile(x.powerSource, x.tabletMode))
                .DisposeWith(disposable);

            // 监听配置文件变更
            config.Performance.PropertyChanged
                .Where(IsActiveProfileProperty)
                .Throttle(TimeSpan.FromMilliseconds(1))
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(_ => ChangeActiveProfile())
                .DisposeWith(disposable);

            config.Performance.ProfileChanged
                .Throttle(TimeSpan.FromMilliseconds(1))
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(UpdateActiveProfile)
                .DisposeWith(disposable);

            // 监听 GPU 模式变化
            atk.GpuMode
                .Skip(1)
                .Throttle(TimeSpan.FromMilliseconds(1))
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(_ => Update())
                .DisposeWith(disposable);
        }

        public PerformanceProfile DefaultProfile { get; }
        public PerformanceProfile TurboProfile { get; }
        public PerformanceProfile SilentProfile { get; }
        public IObservable<PerformanceProfile> ActiveProfile { get; }

        public void Dispose()
        {
            StopTurboLoop();
            disposable?.Dispose();
            disposable = null;
        }

        public void Update()
        {
            ApplyProfile(activeProfileSubject.Value);
        }

        public void SetActiveProfile(Guid id)
        {
            if (id == TurboProfile.Id)
            {
                // 启动或重启 Turbo 循环
                StartTurboLoop();
            }
            else
            {
                // 停止 Turbo 循环
                StopTurboLoop();
            }

            // 广播档位切换
            activeProfileSubject.OnNext(GetProfile(id));
        }

        private void StartTurboLoop()
        {
            // 取消已有循环
            turboCts?.Cancel();
            var cts = new CancellationTokenSource();
            turboCts = cts;

            Task.Run(async () =>
            {
                // 立即设置一次 Turbo
                activeProfileSubject.OnNext(TurboProfile);
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);
                        if (cts.Token.IsCancellationRequested) break;
                        activeProfileSubject.OnNext(TurboProfile);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }, cts.Token);
        }

        private void StopTurboLoop()
        {
            turboCts?.Cancel();
            turboCts = null;
        }

        public PerformanceProfile GetProfile(Guid id)
        {
            if (id == DefaultProfile.Id)
                return DefaultProfile;
            if (id == TurboProfile.Id)
                return TurboProfile;
            if (id == SilentProfile.Id)
                return SilentProfile;
            return config.Performance[id] ?? DefaultProfile;
        }

        private async void ChangeActiveProfile()
        {
            var powerSource = await powerManagement.PowerSource.FirstAsync();
            var tabletMode = await atk.TabletMode.FirstAsync();
            ChangeActiveProfile(powerSource, tabletMode);
        }

        private void ChangeActiveProfile(PowerSource powerSource, TabletMode tabletMode)
        {
            Guid id = tabletMode == TabletMode.Tablet
                ? config.Performance.TabletProfile
                : (powerSource == PowerSource.Battery
                    ? config.Performance.ChargerProfile
                    : config.Performance.BatteryProfile);

            if (activeProfileSubject.Value.Id != id)
                SetActiveProfile(id);
        }

        private void UpdateActiveProfile(Guid changedProfileId)
        {
            if (activeProfileSubject.Value.Id != changedProfileId)
                return;

            SetActiveProfile(changedProfileId);
        }

        private bool IsActiveProfileProperty(string? propertyName)
        {
            return propertyName == nameof(PerformanceConfig.ChargerProfile)
                || propertyName == nameof(PerformanceConfig.BatteryProfile)
                || propertyName == nameof(PerformanceConfig.TabletProfile);
        }

        private async void ApplyProfile(PerformanceProfile profile)
        {
            atk.SetPerformanceMode(profile.PerformanceMode);
            if (profile.IsUserProfile && !await SetCustomProfile(profile))
            {
                Common.TraceWarning("Can't set custom profile");
                activeProfileSubject.OnNext(DefaultProfile);
            }
        }

        private async Task<bool> SetCustomProfile(PerformanceProfile profile)
        {
            var gpuMode = await atk.GpuMode.FirstAsync();
            if (profile.UseCustomFanCurves && (gpuMode == GpuMode.dGpu || config.Common.ForceCustomFanCurves))
            {
                if (!atk.SetFanCurve(FanType.Cpu, profile.CpuFanCurve))
                {
                    Common.TraceWarning("Can't set CPU Fan Curve");
                    return false;
                }
                if (!atk.SetFanCurve(FanType.Gpu, profile.GpuFanCurve))
                {
                    Common.TraceWarning("Can't set GPU Fan Curve");
                    return false;
                }
            }
            if (!atk.SetCpuLimit(profile.CpuLimit))
            {
                Common.TraceWarning("Can't set CPU Power Limit");
                return false;
            }
            return true;
        }
    }
}
